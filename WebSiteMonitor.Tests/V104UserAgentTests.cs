using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class V104UserAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV104-" + Guid.NewGuid().ToString("N"));
    public V104UserAgentTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private Database NewDatabase()
    {
        var db = new Database(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
        db.Initialize();
        return db;
    }

    private static Site NewSite() => new() { Name = "UA test", Url = "https://example.test/", MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" };

    [Fact]
    public void SchemaThreeMigratesToFourWithoutChangingExistingSiteOrHistory()
    {
        var path = Path.Combine(_root, "schema3.db");
        var db = new Database(path);
        db.Initialize();
        var site = NewSite();
        db.SaveSite(site);
        db.ApplySuccess(site.Id, "one", "one", null, null, MonitorMode.Text, DateTimeOffset.Now);
        db.ApplySuccess(site.Id, "two", "two", null, null, MonitorMode.Text, DateTimeOffset.Now);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "ALTER TABLE Sites DROP COLUMN UseBrowserCompatibleUserAgent; UPDATE SchemaVersion SET Version=3;";
            command.ExecuteNonQuery();
        }
        db.Initialize();
        db.Initialize();
        Assert.Equal(7, db.GetSchemaVersion());
        Assert.False(db.GetSite(site.Id)!.UseBrowserCompatibleUserAgent);
        Assert.Equal("two", db.GetSite(site.Id)!.LastHash);
        Assert.Single(db.GetHistory());
    }

    [Fact]
    public void BrowserModeIsOptInAndPersists()
    {
        var db = NewDatabase();
        var site = NewSite();
        Assert.False(site.UseBrowserCompatibleUserAgent);
        db.SaveSite(site);
        Assert.False(db.GetSite(site.Id)!.UseBrowserCompatibleUserAgent);
        site.UseBrowserCompatibleUserAgent = true;
        db.SaveSite(site);
        Assert.True(db.GetSite(site.Id)!.UseBrowserCompatibleUserAgent);
    }

    [Fact]
    public async Task StandardForbiddenAndBrowserCompatibleSuccessUseOnlySelectedUserAgent()
    {
        var handler = new UaHandler();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WebSiteMonitor/1.1.0 (+Windows 11; portable monitor)");
        using var fetcher = new SharedHttpFetcher(client);
        var db = NewDatabase();
        var site = NewSite();
        db.SaveSite(site);
        var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "logs")));
        var blocked = await engine.CheckAsync(site, CancellationToken.None);
        Assert.Equal(CheckOutcome.Failed, blocked.Outcome);
        Assert.Contains("ブラウザー互換User-Agent", blocked.Message);
        Assert.All(handler.Seen.Take(3), ua => Assert.StartsWith("WebSiteMonitor/1.1.0", ua));
        site = db.GetSite(site.Id)!;
        site.UseBrowserCompatibleUserAgent = true;
        db.SaveSite(site);
        var baseline = await engine.CheckAsync(site, CancellationToken.None);
        Assert.Equal(CheckOutcome.BaselineCreated, baseline.Outcome);
        Assert.False(baseline.ShouldNotify);
        Assert.Equal(SharedHttpFetcher.BrowserCompatibleUserAgent, handler.Seen.Last());
        Assert.Equal(1, db.GetSite(site.Id)!.MonitorRevision);
    }

    [Fact]
    public void TogglingUaResetsConditionalHeadersBaselineErrorAndSchedulesImmediateCheck()
    {
        var db = NewDatabase();
        var site = NewSite();
        db.SaveSite(site);
        db.ApplySuccess(site.Id, "old", "old", "\"etag\"", "Tue, 01 Sep 2026 00:00:00 GMT", MonitorMode.Text, DateTimeOffset.Now);
        var oldRevision = db.GetSite(site.Id)!.MonitorRevision;
        site = db.GetSite(site.Id)!;
        site.UseBrowserCompatibleUserAgent = true;
        var before = DateTimeOffset.Now;
        db.SaveSite(site);
        var after = db.GetSite(site.Id)!;
        Assert.Equal(oldRevision + 1, after.MonitorRevision);
        Assert.Null(after.LastETag);
        Assert.Null(after.LastModified);
        Assert.Null(after.LastHash);
        Assert.Null(after.LastPreview);
        Assert.Null(after.LastError);
        Assert.Equal(0, after.ConsecutiveErrors);
        Assert.Null(after.AutoDetectedFeedUrl);
        Assert.InRange(after.NextDue!.Value, before.AddSeconds(-2), DateTimeOffset.Now.AddSeconds(2));
        Assert.Equal(CheckOutcome.Discarded, db.ApplySuccess(site.Id, oldRevision, "stale", "stale", null, null, MonitorMode.Text, DateTimeOffset.Now).Outcome);
        Assert.Null(db.GetSite(site.Id)!.LastHash);
    }

    private sealed class UaHandler : HttpMessageHandler
    {
        public readonly List<string> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ua = request.Headers.UserAgent.ToString();
            Seen.Add(ua);
            return Task.FromResult(ua == SharedHttpFetcher.BrowserCompatibleUserAgent
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>fresh baseline</html>", Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}
