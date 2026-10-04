using System.Net;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class V109RedactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV109-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "test.db");

    public V109RedactionTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("System.Exception: server response:\n{\"authsystem_feed_token_value\":\"TEST_SECRET\"}")]
    [InlineData("System.Exception: %61%75%74%68system_feed_%74oken_value=TEST_SECRET")]
    [InlineData("System.Exception: outer\n---> System.Exception: {\"access_token\":\"SECOND_SECRET\"}\nserver response: authsystem_feed_token_value=TEST_SECRET")]
    [InlineData("server response: authsystem_feed_token_value=TEST_SECRET")]
    public async Task ParameterErrorsAreSafeInResultRawDatabaseAndActualLog(string message)
    {
        var redacted = NotificationUrl.RedactText(message);
        Assert.DoesNotContain("TEST_SECRET", redacted);
        Assert.DoesNotContain("SECOND_SECRET", redacted);
        Assert.Contains("[SECRET_REDACTED]", redacted);
        await AssertOutputBoundariesAsync(message, "TEST_SECRET", "SECOND_SECRET");
    }

    [Fact]
    public void QuotedEncodedAndEscapedKeyVariantsAreRedactedTogether()
    {
        string[] messages =
        [
            "\"authsystem_feed_token_value\": \"TEST_SECRET\"",
            "{\"api_key\": \"TEST_SECRET\"}",
            "System.Exception: %61%63%63%65%73%73%5f%74%6f%6b%65%6e=TEST_SECRET",
            "---> System.Exception: {\"%41cCeSs%5fToKeN\" :\n\"TEST_SECRET\"}",
            "server response: token%2Did='TEST_SECRET'",
            "{\"%2561%2575%2574%2568system_feed_token_value\":\"TEST_SECRET\"}",
            "{\"a\\u0075thsystem_feed_token_value\":\"TEST_SECRET\\\" SECOND_SECRET\"}",
            "System.Exception: private_key=TEST_SECRET&pwd=SECOND_SECRET"
        ];
        var logger = new FileLogger(Path.Combine(_root, "variants"));
        foreach (var message in messages)
        {
            var redacted = NotificationUrl.RedactText(message);
            Assert.DoesNotContain("TEST_SECRET", redacted);
            Assert.DoesNotContain("SECOND_SECRET", redacted);
            Assert.Contains("[SECRET_REDACTED]", redacted);
            logger.Info(message);
        }
        var saved = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "variants"), "*.log").Select(File.ReadAllText));
        Assert.DoesNotContain("TEST_SECRET", saved);
        Assert.DoesNotContain("SECOND_SECRET", saved);
    }

    [Fact]
    public async Task UnknownMalformedOrOversizedExternalErrorsUseOnlySafeDiagnostics()
    {
        string[] messages =
        [
            "server says personal record: PRIVATE_RECORD_VALUE name@example.test",
            "System.Exception: %ZZauth=TEST_SECRET",
            "server response: {\"access_token\":\"TEST_SECRET",
            new string('a', 65537) + " PRIVATE_RECORD_VALUE"
        ];
        foreach (var message in messages)
            await AssertOutputBoundariesAsync(message, "TEST_SECRET", "PRIVATE_RECORD_VALUE", "name@example.test");
    }

    [Fact]
    public async Task HttpStatusIsRetainedWithoutExternalResponseBody()
    {
        var outputs = await AssertOutputBoundariesAsync("private body PRIVATE_RECORD_VALUE", "PRIVATE_RECORD_VALUE",
            error: new HttpRequestException("private body PRIVATE_RECORD_VALUE", null, HttpStatusCode.BadGateway));
        Assert.Contains("502", outputs);
        Assert.Contains("HttpRequestException", outputs);
    }

    [Fact]
    public async Task FeedFallbackLogsDoNotRetainUnstructuredExternalExceptionText()
    {
        var database = new Database(DatabasePath);
        database.Initialize();
        var site = new Site { Name = "Feedフォールバック検査", Url = "https://example.test/page/", MonitorMode = MonitorMode.Auto,
            AutoDetectedFeedUrl = "https://example.test/feed/" };
        database.SaveSite(site);
        // Two failures exercise the INFO fallback boundary and the final error boundaries.
        var engine = new MonitorEngine(database, new FailingFetcher(new InvalidOperationException("PRIVATE_RECORD_VALUE")),
            new FileLogger(Path.Combine(_root, "logs")));
        var result = await engine.CheckAsync(site, default);
        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        var log = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "logs"), "*.log").Select(File.ReadAllText));
        Assert.Contains("本文へフォールバック", log);
        Assert.DoesNotContain("PRIVATE_RECORD_VALUE", log);
        Assert.DoesNotContain("PRIVATE_RECORD_VALUE", result.Message);
        Assert.DoesNotContain("PRIVATE_RECORD_VALUE", database.GetSite(site.Id)!.LastError!);
    }

    private async Task<string> AssertOutputBoundariesAsync(string message, string secret, string? secondSecret = null,
        string? thirdSecret = null, Exception? error = null)
    {
        var database = new Database(DatabasePath);
        database.Initialize();
        var site = new Site { Name = "ローカル秘匿検査", Url = "https://example.test/feed/", MonitorMode = MonitorMode.Text };
        database.SaveSite(site);
        var logs = Path.Combine(_root, "logs");
        var engine = new MonitorEngine(database, new FailingFetcher(error ?? new InvalidOperationException(message)), new FileLogger(logs));
        var result = await engine.CheckAsync(site, default);
        Assert.Equal(CheckOutcome.Failed, result.Outcome);

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastError FROM Sites WHERE Id=$id";
        command.Parameters.AddWithValue("$id", site.Id);
        var stored = Convert.ToString(command.ExecuteScalar()) ?? "";
        Assert.NotEmpty(stored);
        Assert.Contains("[DIAGNOSTIC_REDACTED]", stored);
        Assert.Equal(result.Message, stored);
        var log = string.Join("\n", Directory.GetFiles(logs, "*.log").Select(File.ReadAllText));
        Assert.NotEmpty(log);
        var outputs = result.Message + "\n" + stored + "\n" + log;
        foreach (var value in new[] { secret, secondSecret, thirdSecret }.OfType<string>())
            Assert.DoesNotContain(value, outputs);
        return outputs;
    }

    private sealed class FailingFetcher(Exception error) : IHttpFetcher
    {
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
            => throw error;
    }
}
