namespace WebSiteMonitor.Core;

public sealed class MonitorEngine
{
    private readonly Database _database;
    private readonly IHttpFetcher _fetcher;
    private readonly FileLogger _logger;

    public MonitorEngine(Database database, IHttpFetcher fetcher, FileLogger logger)
    {
        _database = database;
        _fetcher = fetcher;
        _logger = logger;
    }

    public async Task<CheckResult> CheckAsync(Site requestedSite, CancellationToken cancellationToken)
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
                fetched = await _fetcher.FetchAsync(requestedUri, site.LastETag, site.LastModified, site.UseBrowserCompatibleUserAgent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (usingAutoDetectedFeed && !cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"自動検出Feedが無効なため本文へフォールバック SiteId={site.Id}: {ex.Message}");
                usingAutoDetectedFeed = false;
                updateAutoDetectedFeed = true;
                fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(site.Url), null, null, site.UseBrowserCompatibleUserAgent, cancellationToken).ConfigureAwait(false);
            }

            if (fetched.NotModified)
            {
                if (!_database.ApplyNotModified(site.Id, revision, now)) return Discarded(site.Id);
                return new CheckResult(CheckOutcome.NotModified, site, "HTTP 304: 更新なし", MonitorRevision: revision);
            }

            var text = SharedHttpFetcher.DecodeBody(fetched);
            if (usingAutoDetectedFeed && !FeedParser.LooksLikeFeed(fetched.MediaType, text))
            {
                _logger.Info($"自動検出Feedの応答がFeedではないため本文へフォールバック SiteId={site.Id}");
                usingAutoDetectedFeed = false;
                updateAutoDetectedFeed = true;
                fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(site.Url), null, null, site.UseBrowserCompatibleUserAgent, cancellationToken).ConfigureAwait(false);
                text = SharedHttpFetcher.DecodeBody(fetched);
            }

            var effective = site.MonitorMode;
            string content;
            if (site.MonitorMode == MonitorMode.Feed || (site.MonitorMode == MonitorMode.Auto && FeedParser.LooksLikeFeed(fetched.MediaType, text)))
            {
                content = FeedParser.ParseAndNormalize(text);
                effective = MonitorMode.Feed;
            }
            else if (site.MonitorMode == MonitorMode.Auto)
            {
                var detected = ContentExtractor.DetectFeedUrl(text, fetched.FinalUri);
                if (detected is not null)
                {
                    try
                    {
                        var feed = await _fetcher.FetchAsync(new Uri(detected), null, null, site.UseBrowserCompatibleUserAgent, cancellationToken).ConfigureAwait(false);
                        content = FeedParser.ParseAndNormalize(SharedHttpFetcher.DecodeBody(feed));
                        effective = MonitorMode.Feed;
                        updateAutoDetectedFeed = true;
                        autoDetectedFeedUrl = detected;
                        fetched = feed;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Info($"検出Feedが無効なため本文へフォールバック SiteId={site.Id}: {ex.Message}");
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

            var hash = ContentHasher.Sha256(content);
            var result = _database.ApplySuccess(site.Id, revision, hash, ContentHasher.Preview(content), fetched.ETag, fetched.LastModified, effective, now, updateAutoDetectedFeed, autoDetectedFeedUrl);
            if (result.Outcome == CheckOutcome.Discarded)
            {
                _logger.Info($"監視設定変更のため取得結果を破棄 SiteId={site.Id}");
                return result;
            }
            if (result.Outcome == CheckOutcome.Changed) _logger.Info($"更新検出 SiteId={site.Id} Name={site.Name}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } && !site.UseBrowserCompatibleUserAgent
                ? "403 Forbidden：必要に応じて『ブラウザー互換User-Agentを使用する』を試してください。"
                : ex.Message;
            if (!_database.ApplyError(site.Id, revision, error, now)) return Discarded(site.Id);
            _logger.Error($"監視失敗 SiteId={site.Id} Name={site.Name}", ex);
            return new CheckResult(CheckOutcome.Failed, site, error, MonitorRevision: revision);
        }
    }

    public async Task<ExtractionResult> TestExtractAsync(Site site, CancellationToken cancellationToken)
    {
        SiteValidation.Validate(site);
        var requested = site.MonitorMode == MonitorMode.Feed && !string.IsNullOrWhiteSpace(site.FeedUrl) ? site.FeedUrl : site.Url;
        var fetched = await _fetcher.FetchAsync(SiteValidation.ValidateHttpUrl(requested), null, null, site.UseBrowserCompatibleUserAgent, cancellationToken).ConfigureAwait(false);
        var text = SharedHttpFetcher.DecodeBody(fetched);
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