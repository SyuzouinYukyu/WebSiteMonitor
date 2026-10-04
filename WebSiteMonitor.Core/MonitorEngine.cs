using System.Diagnostics;

namespace WebSiteMonitor.Core;

public sealed class MonitorEngine
{
    private readonly Database _database;
    private readonly IHttpFetcher _fetcher;
    private readonly FileLogger _logger;
    private readonly Func<bool> _notificationsEnabled;
    private readonly TimeSpan _monitorTimeout;
    private readonly TimeSpan _testTimeout;

    public MonitorEngine(Database database, IHttpFetcher fetcher, FileLogger logger, Func<bool>? notificationsEnabled = null,
        TimeSpan? monitorTimeout = null, TimeSpan? testTimeout = null)
    {
        _database = database;
        _fetcher = fetcher;
        _logger = logger;
        _notificationsEnabled = notificationsEnabled ?? (() => true);
        _monitorTimeout = monitorTimeout ?? TimeSpan.FromSeconds(100);
        _testTimeout = testTimeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<CheckResult> CheckAsync(Site requestedSite, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_monitorTimeout);
        var elapsed = Stopwatch.StartNew();
        var site = _database.GetSite(requestedSite.Id) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        try { return await CheckCoreAsync(site, deadline.Token, elapsed).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string error = "監視処理が制限時間を超えました。前回の基準値は保持しています。";
            if (!_database.ApplyError(site.Id, site.MonitorRevision, error, DateTimeOffset.Now)) return Discarded(site.Id);
            _logger.Error($"監視期限超過 SiteId={site.Id}");
            return new CheckResult(CheckOutcome.Failed, site, error, MonitorRevision: site.MonitorRevision);
        }
    }

    private async Task<CheckResult> CheckCoreAsync(Site requestedSite, CancellationToken cancellationToken, Stopwatch elapsed)
    {
        var now = DateTimeOffset.Now;
        var site = _database.GetSite(requestedSite.Id) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        var revision = site.MonitorRevision;
        try
        {
            SiteValidation.Validate(site);
            var storedFeedUrl = site.MonitorMode switch
            {
                MonitorMode.Feed => site.FeedUrl,
                MonitorMode.Auto => site.AutoDetectedFeedUrl,
                _ => null
            };
            var usingAutoDetectedFeed = site.MonitorMode == MonitorMode.Auto && !string.IsNullOrWhiteSpace(storedFeedUrl);
            var updateAutoDetectedFeed = false;
            string? autoDetectedFeedUrl = null;
            var requestedUri = SiteValidation.ValidateHttpUrl(storedFeedUrl ?? site.Url);
            HttpFetchResult fetched;
            try
            {
                fetched = await _fetcher.FetchAsync(requestedUri, site.LastETag, site.LastModified, site.UseBrowserCompatibleUserAgent, _monitorTimeout - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (usingAutoDetectedFeed && !cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"自動検出Feedが無効なため本文へフォールバック SiteId={site.Id}: {NotificationUrl.SafeException(ex)}");
                usingAutoDetectedFeed = false;
                updateAutoDetectedFeed = true;
                fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(site.Url), null, null, site.UseBrowserCompatibleUserAgent, _monitorTimeout - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);
            }

            if (fetched.NotModified)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_database.ApplyNotModified(site.Id, revision, now)) return Discarded(site.Id);
                return new CheckResult(CheckOutcome.NotModified, site, "HTTP 304: 更新なし", MonitorRevision: revision);
            }

            var text = SharedHttpFetcher.DecodeBody(fetched);
            cancellationToken.ThrowIfCancellationRequested();
            if (usingAutoDetectedFeed && !FeedParser.LooksLikeFeed(fetched.MediaType, text))
            {
                _logger.Info($"自動検出Feedの応答がFeedではないため本文へフォールバック SiteId={site.Id}");
                usingAutoDetectedFeed = false;
                updateAutoDetectedFeed = true;
                fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(site.Url), null, null, site.UseBrowserCompatibleUserAgent, _monitorTimeout - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);
                text = SharedHttpFetcher.DecodeBody(fetched);
            }

            var effective = site.MonitorMode;
            string? notificationFeed = null;
            string content;
            if (site.MonitorMode == MonitorMode.Feed || (site.MonitorMode == MonitorMode.Auto && FeedParser.LooksLikeFeed(fetched.MediaType, text)))
            {
                content = FeedParser.ParseAndNormalize(text);
                effective = MonitorMode.Feed;
                notificationFeed = text;
            }
            else if (site.MonitorMode == MonitorMode.Auto)
            {
                var detected = ContentExtractor.DetectFeedUrl(text, fetched.FinalUri);
                if (detected is not null)
                {
                    try
                    {
                        var feed = await _fetcher.FetchAsync(new Uri(detected), null, null, site.UseBrowserCompatibleUserAgent, _monitorTimeout - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);
                        notificationFeed = SharedHttpFetcher.DecodeBody(feed);
                        content = FeedParser.ParseAndNormalize(notificationFeed);
                        effective = MonitorMode.Feed;
                        updateAutoDetectedFeed = true;
                        autoDetectedFeedUrl = detected;
                        fetched = feed;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Info($"検出Feedが無効なため本文へフォールバック SiteId={site.Id}: {NotificationUrl.SafeException(ex)}");
                        content = ContentExtractor.ExtractText(text);
                        effective = MonitorMode.Text;
                    }
                }
                else
                {
                    content = ContentExtractor.ExtractText(text);
                    effective = MonitorMode.Text;
                }
            }
            else
            {
                content = ContentExtractor.Extract(text, site.MonitorMode, site.Selector, site.XPath, site.Regex).Content;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var hash = ContentHasher.Sha256(content);
            var preview = ContentHasher.SafePreview(content, effective);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _database.ApplySuccess(site.Id, revision, hash, preview, fetched.ETag, fetched.LastModified, effective, now, updateAutoDetectedFeed, autoDetectedFeedUrl,
                _notificationsEnabled(), NotificationUrl.ForSite(site));
            if (result.Outcome == CheckOutcome.Discarded)
            {
                _logger.Info($"監視設定変更のため取得結果を破棄 SiteId={site.Id}");
                return result;
            }
            if (result.Outcome == CheckOutcome.Changed) _logger.UpdateDetected(site.Name, site.Url, DateTimeOffset.Now);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } && !site.UseBrowserCompatibleUserAgent
                ? "403 Forbidden：必要に応じて『ブラウザー互換User-Agentを使用する』を試してください。"
                : ex is TimeoutException ? "HTTP取得が制限時間を超えました。前回の基準値は保持しています。" : NotificationUrl.SafeException(ex);
            if (!_database.ApplyError(site.Id, revision, error, now)) return Discarded(site.Id);
            _logger.Error($"監視失敗 SiteId={site.Id} Name={site.Name}", ex);
            return new CheckResult(CheckOutcome.Failed, site, error, MonitorRevision: revision);
        }
    }

    public async Task<ExtractionResult> TestExtractAsync(Site site, CancellationToken cancellationToken, IProgress<string>? progress = null, int? previewLength = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_testTimeout);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = await TestExtractCoreAsync(site, deadline.Token, progress, elapsed).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (previewLength is { } maximum)
            {
                progress?.Report("プレビュー整形");
                result = result with { Content = ContentHasher.SafePreview(result.Content, result.EffectiveMode, maximum) };
            }
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("監視テストが制限時間を超えました。"); }
    }

    private async Task<ExtractionResult> TestExtractCoreAsync(Site site, CancellationToken cancellationToken, IProgress<string>? progress, Stopwatch elapsed)
    {
        SiteValidation.Validate(site);
        progress?.Report("HTTP取得（ヘッダー / 本文）");
        var requested = site.MonitorMode == MonitorMode.Feed && !string.IsNullOrWhiteSpace(site.FeedUrl) ? site.FeedUrl : site.Url;
        var fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(requested), null, null, site.UseBrowserCompatibleUserAgent, _testTimeout - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report($"文字コード変換（{fetched.Body?.Length ?? 0}バイト）");
        var text = SharedHttpFetcher.DecodeBody(fetched);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("抽出処理");
        if (site.MonitorMode == MonitorMode.Feed) return new ExtractionResult(FeedParser.ParseAndNormalize(text), MonitorMode.Feed);
        if (site.MonitorMode == MonitorMode.Auto)
        {
            if (FeedParser.LooksLikeFeed(fetched.MediaType, text)) return new ExtractionResult(FeedParser.ParseAndNormalize(text), MonitorMode.Feed);
            var detected = ContentExtractor.DetectFeedUrl(text, fetched.FinalUri);
            if (detected is not null) return new ExtractionResult($"Feedを検出しました: {detected}", MonitorMode.Feed, detected);
            return new ExtractionResult(ContentExtractor.ExtractText(text), MonitorMode.Text);
        }
        return ContentExtractor.Extract(text, site.MonitorMode, site.Selector, site.XPath, site.Regex);
    }

    private CheckResult Discarded(long siteId)
    {
        var current = _database.GetSite(siteId) ?? throw new InvalidOperationException("監視サイトが見つかりません。");
        return new CheckResult(CheckOutcome.Discarded, current, "監視設定が変更されたため取得結果を破棄しました", MonitorRevision: current.MonitorRevision);
    }
}
