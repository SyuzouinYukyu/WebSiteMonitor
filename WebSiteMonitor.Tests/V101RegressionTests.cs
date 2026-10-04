using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class V101RegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV101Tests-" + Guid.NewGuid().ToString("N"));

    public V101RegressionTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Theory]
    [InlineData("url")]
    [InlineData("mode")]
    [InlineData("feed")]
    [InlineData("selector")]
    [InlineData("xpath")]
    [InlineData("regex")]
    public void MonitoringConfigurationChangeResetsBaselineAndAutoFeed(string kind)
    {
        var database = NewDatabase();
        var site = ValidSite();
        database.SaveSite(site);
        database.ApplySuccess(site.Id, "OLD", "old preview", "etag", "modified", MonitorMode.Text, DateTimeOffset.Now);
        database.SaveDetectedFeed(site.Id, "https://example.test/auto.xml");
        var changed = database.GetSite(site.Id)!;
        switch (kind)
        {
            case "url": changed.Url = "https://example.test/new"; break;
            case "mode": changed.MonitorMode = MonitorMode.FullPage; break;
            case "feed": changed.FeedUrl = "https://example.test/other.xml"; break;
            case "selector": changed.MonitorMode = MonitorMode.CssSelector; changed.Selector = ".new"; break;
            case "xpath": changed.MonitorMode = MonitorMode.XPath; changed.XPath = "//article"; break;
            case "regex": changed.MonitorMode = MonitorMode.Regex; changed.Regex = "new"; break;
        }
        database.SaveSite(changed);
        var stored = database.GetSite(site.Id)!;
        Assert.Null(stored.AutoDetectedFeedUrl);
        Assert.Null(stored.LastHash);
        Assert.Null(stored.LastPreview);
        Assert.Null(stored.LastETag);
        Assert.Null(stored.LastModified);
        Assert.Null(stored.LastNotifiedHash);
        Assert.Null(stored.LastError);
        Assert.Equal(0, stored.ConsecutiveErrors);
        Assert.Null(stored.EffectiveMode);
    }

    [Fact]
    public void AutoUrlChangeCannotReuseOldDetectedFeed()
    {
        var database = NewDatabase();
        var site = ValidSite();
        site.MonitorMode = MonitorMode.Auto;
        database.SaveSite(site);
        database.SaveDetectedFeed(site.Id, "https://example.test/old-feed.xml");
        var changed = database.GetSite(site.Id)!;
        changed.Url = "https://example.test/replaced";
        database.SaveSite(changed);
        var stored = database.GetSite(site.Id)!;
        Assert.Null(stored.AutoDetectedFeedUrl);
        Assert.Null(stored.FeedUrl);
    }

    [Fact]
    public void V100DatabaseMigratesExplicitAndDetectedFeedSeparately()
    {
        var path = Path.Combine(_temp, "v100.db");
        CreateV100Database(path, "https://example.test/auto.xml", MonitorMode.Auto);
        var database = new Database(path);
        database.Initialize();
        var site = Assert.Single(database.GetSites());
        Assert.Equal(7, database.GetSchemaVersion());
        Assert.Null(site.FeedUrl);
        Assert.Equal("https://example.test/auto.xml", site.AutoDetectedFeedUrl);
    }

    [Fact]
    public async Task SameSiteGateSerializesAndEvictsIdleKey()
    {
        var gate = new KeyedSiteGate();
        var active = 0;
        var maximum = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Work()
        {
            using var lease = await gate.AcquireAsync(17, CancellationToken.None);
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await release.Task;
            Interlocked.Decrement(ref active);
        }
        var first = Work();
        var second = Work();
        await Task.Delay(50);
        Assert.Equal(1, maximum);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximum);
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task DifferentSiteIdsCanRunInParallel()
    {
        var gate = new KeyedSiteGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        async Task Work(long id)
        {
            using var lease = await gate.AcquireAsync(id, CancellationToken.None);
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
        }
        var first = Work(1);
        var second = Work(2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task SchedulerStopCancelsActiveCheckBeforeResourceDisposal()
    {
        var database = NewDatabase();
        var site = ValidSite();
        database.SaveSite(site);
        var fetcher = new WaitingFetcher();
        var engine = new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs")));
        using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, engine);
        var check = scheduler.CheckNowAsync([site]);
        await fetcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.StopAsync(TimeSpan.FromSeconds(2));
        await check;
        Assert.Equal(0, scheduler.RunningCount);
    }

    [Theory]
    [InlineData("div[")]
    [InlineData("a,,")]
    public void InvalidCssIsRejected(string selector) => Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateCssSelector(selector));

    [Theory]
    [InlineData("//*[")]
    [InlineData("//div[")]
    public void InvalidXPathIsRejected(string xpath) => Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateXPath(xpath));

    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    public void InvalidRegexIsRejected(string pattern) => Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateRegex(pattern));

    [Fact]
    public void NotificationMasterDisablesEveryChannel()
    {
        Assert.False(NotificationPolicy.ShouldDispatch(false, true));
        Assert.False(NotificationPolicy.ShouldDispatch(false, false));
        Assert.True(NotificationPolicy.ShouldDispatch(true, true));
        Assert.False(NotificationPolicy.ShouldDispatch(true, false));
    }

    [Fact]
    public void CharsetPrioritySupportsHttpHeaderAndShiftJisMeta()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var header = new HttpFetchResult(200, Encoding.GetEncoding(932).GetBytes("日本語"), "text/html", "windows-31j", null, null, new Uri("https://example.test/"));
        var metaHtml = "<html><head><meta charset='Shift_JIS'></head><body>日本語</body></html>";
        var meta = new HttpFetchResult(200, Encoding.GetEncoding(932).GetBytes(metaHtml), "text/html", null, null, null, new Uri("https://example.test/"));
        Assert.Equal("日本語", SharedHttpFetcher.DecodeBody(header));
        Assert.Contains("日本語", SharedHttpFetcher.DecodeBody(meta));
    }

    [Fact]
    public void CharsetHeaderWinsOverMetaAndBomWinsOverHeader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var headerWins = new HttpFetchResult(200, Encoding.UTF8.GetBytes("<meta charset='Shift_JIS'>日本語"), "text/html", "utf-8", null, null, new Uri("https://example.test/"));
        var bomBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("日本語")).ToArray();
        var bomWins = new HttpFetchResult(200, bomBytes, "text/html", "utf-8", null, null, new Uri("https://example.test/"));
        Assert.Contains("日本語", SharedHttpFetcher.DecodeBody(headerWins));
        Assert.Equal("日本語", SharedHttpFetcher.DecodeBody(bomWins));
    }

    [Fact]
    public void RetryAfterSupportsDeltaAndHttpDateWithCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), RetryPolicy.DelayForAttempt(0, TimeSpan.FromSeconds(12)));
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        Assert.Equal(TimeSpan.FromSeconds(45), RetryPolicy.DelayForAttempt(0, null, now.AddSeconds(45), now));
        Assert.Equal(TimeSpan.FromHours(1), RetryPolicy.DelayForAttempt(0, null, now.AddDays(10), now));
    }

    [Fact]
    public void FeedUpdatedAndContentChangesAlterHash()
    {
        const string first = "<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>x</id><title>T</title><updated>2026-01-01</updated><content>A</content></entry></feed>";
        const string second = "<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>x</id><title>T</title><updated>2026-01-02</updated><content>B</content></entry></feed>";
        var normalized = FeedParser.ParseAndNormalize(second);
        Assert.NotEqual(ContentHasher.Sha256(FeedParser.ParseAndNormalize(first)), ContentHasher.Sha256(normalized));
        Assert.Contains("updated=2026-01-02", normalized);
        Assert.Contains("content=B", normalized);
    }

    [Fact]
    public void SettingsV100DefaultsWindowsIntegrationToEnabled()
    {
        var path = Path.Combine(_temp, "settings.json");
        File.WriteAllText(path, "{\"StartWithWindows\":true,\"NotificationsEnabled\":false}");
        var settings = new SettingsStore(path).Load();
        Assert.True(settings.WindowsIntegrationEnabled);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.NotificationsEnabled);
    }

    [Fact]
    public void PopupStackPositionsDoNotOverlapWithinWorkArea()
    {
        var workArea = new Rectangle(0, 0, 1000, 700);
        var positions = global::WebSiteMonitor.PopupStackLayout.Calculate(workArea, [new Size(420, 180), new Size(420, 180), new Size(420, 180)]);
        Assert.All(positions, rectangle => Assert.True(workArea.Contains(rectangle)));
        Assert.False(positions[0].IntersectsWith(positions[1]));
        Assert.False(positions[1].IntersectsWith(positions[2]));
    }

    [Fact]
    public void ClipboardNormalizationRemovesOnlyRequestedWhitespace()
    {
        Assert.Equal("名前", global::WebSiteMonitor.ClipboardText.NormalizeSiteName("名前\r\n"));
        Assert.Equal("https://example.test/path", global::WebSiteMonitor.ClipboardText.NormalizeUrl(" \r\nhttps://example.test/path\n "));
    }

    private Database NewDatabase()
    {
        var database = new Database(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }

    private static Site ValidSite() => new()
    {
        Name = "Test", Url = "https://example.test/", MonitorMode = MonitorMode.Feed, FeedUrl = "https://example.test/feed.xml",
        ScheduleMode = ScheduleMode.Interval, IntervalMinutes = 60, DailyTime = "09:00", Selector = ".old", XPath = "//main", Regex = "old"
    };

    private static void CreateV100Database(string path, string feedUrl, MonitorMode mode)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
            INSERT INTO SchemaVersion VALUES(1);
            CREATE TABLE Sites (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Url TEXT NOT NULL, Enabled INTEGER NOT NULL,
              MonitorMode INTEGER NOT NULL, FeedUrl TEXT, Selector TEXT, XPath TEXT, Regex TEXT,
              ScheduleMode INTEGER NOT NULL, IntervalMinutes INTEGER NOT NULL, DailyTime TEXT NOT NULL,
              WindowsNotification INTEGER NOT NULL, PopupNotification INTEGER NOT NULL, SoundNotification INTEGER NOT NULL,
              SoundFile TEXT, SoundVolume INTEGER NOT NULL, LastHash TEXT, LastPreview TEXT, LastETag TEXT, LastModified TEXT,
              LastChecked TEXT, LastChanged TEXT, LastNotifiedHash TEXT, NextDue TEXT, ConsecutiveErrors INTEGER NOT NULL DEFAULT 0,
              LastError TEXT, EffectiveMode TEXT);
            INSERT INTO Sites(Name,Url,Enabled,MonitorMode,FeedUrl,ScheduleMode,IntervalMinutes,DailyTime,WindowsNotification,PopupNotification,SoundNotification,SoundVolume,ConsecutiveErrors)
            VALUES('old','https://example.test/',1,$mode,$feed,0,60,'09:00',1,0,0,80,0);
            """;
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.Parameters.AddWithValue("$feed", feedUrl);
        command.ExecuteNonQuery();
    }

    private sealed class WaitingFetcher : IHttpFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the fetch.");
        }
    }
}
