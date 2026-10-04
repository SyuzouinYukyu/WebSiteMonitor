using System.Text;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class V108RedactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV108-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "test.db");
    public V108RedactionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("System.Exception: outer [URL_REDACTED]\n---> System.Exception: authsystem_feed_token_value=TEST_SECRET", "TEST_SECRET")]
    [InlineData("server response: authsystem_feed_token_value=TEST_SECRET", "TEST_SECRET")]
    [InlineData("System.Exception: request failed\n---> System.Exception: authsystem_feed_token_value=TEST_SECRET&access_token=SECOND_SECRET", "TEST_SECRET", "SECOND_SECRET")]
    public async Task NestedAndServerErrorsAreRedactedInResultDatabaseAndLog(string exceptionMessage, params string[] secrets)
    {
        foreach (var secret in secrets) Assert.DoesNotContain(secret, NotificationUrl.RedactText(exceptionMessage));

        var database = new Database(DatabasePath); database.Initialize();
        var site = new Site { Name = "例外検査", Url = "https://example.test/feed/", MonitorMode = MonitorMode.Text };
        database.SaveSite(site);
        var logs = Path.Combine(_root, "logs");
        var engine = new MonitorEngine(database, new FailingFetcher(exceptionMessage), new FileLogger(logs));

        var result = await engine.CheckAsync(site, default);
        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        foreach (var secret in secrets) Assert.DoesNotContain(secret, result.Message);

        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT LastError FROM Sites WHERE Id=$id";
            command.Parameters.AddWithValue("$id", site.Id);
            var stored = Convert.ToString(command.ExecuteScalar()) ?? "";
            foreach (var secret in secrets) Assert.DoesNotContain(secret, stored);
            Assert.Contains("[SECRET_REDACTED]", stored);
        }

        var log = string.Join("\n", Directory.GetFiles(logs, "*.log").Select(File.ReadAllText));
        foreach (var secret in secrets) Assert.DoesNotContain(secret, log);
        Assert.Contains("[SECRET_REDACTED]", log);
        Assert.Contains(
            exceptionMessage.StartsWith("server response", StringComparison.Ordinal)
                ? "server response"
                : "System.Exception",
            log);
    }

    [Theory]
    [InlineData("network timeout while reading response")]
    [InlineData("System.Exception: outer [URL_REDACTED]")]
    public void NonSensitiveDiagnosticTextIsRetained(string message) => Assert.Equal(message, NotificationUrl.RedactText(message));

    private sealed class FailingFetcher(string message) : IHttpFetcher
    {
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
            => throw new InvalidOperationException(message);
    }
}
