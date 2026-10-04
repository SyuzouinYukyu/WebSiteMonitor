using System.Text;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class AutoFallbackTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorAutoTests-" + Guid.NewGuid().ToString("N"));

    public AutoFallbackTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task InvalidDiscoveredFeedFallsBackToPageTextWithoutNotification()
    {
        var database = new Database(Path.Combine(_temp, "test.db"));
        database.Initialize();
        var site = new Site { Name = "Auto", Url = "https://example.test/page", MonitorMode = MonitorMode.Auto, IntervalMinutes = 60, DailyTime = "09:00" };
        database.SaveSite(site);
        var page = "<html><head><link rel='alternate' type='application/rss+xml' href='/bad.xml'></head><body><main>Visible text</main></body></html>";
        var fetcher = new SequenceFetcher([
            new HttpFetchResult(200, Encoding.UTF8.GetBytes(page), "text/html", "utf-8", null, null, new Uri(site.Url)),
            new HttpRequestException("invalid feed")
        ]);
        var engine = new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs")));

        var result = await engine.CheckAsync(site, CancellationToken.None);

        Assert.Equal(CheckOutcome.BaselineCreated, result.Outcome);
        Assert.False(result.ShouldNotify);
        var stored = database.GetSite(site.Id)!;
        Assert.Equal(MonitorMode.Text.ToString(), stored.EffectiveMode);
        Assert.Contains("Visible text", stored.LastPreview);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private sealed class SequenceFetcher(IEnumerable<object> values) : IHttpFetcher
    {
        private readonly Queue<object> _values = new(values);
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken)
        {
            var value = _values.Dequeue();
            return value is Exception error ? Task.FromException<HttpFetchResult>(error) : Task.FromResult((HttpFetchResult)value);
        }
    }
}
