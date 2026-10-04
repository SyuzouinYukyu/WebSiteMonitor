using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V107AuditTests : IDisposable
{
    private const string SecretUrl = "https://example.test/feed/?authsystem_feed_token=TEST_SECRET";
    private const string Destination = "https://example.test/closed_stack/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV107-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "test.db");
    public V107AuditTests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
    private Database NewDb() { var db = new Database(DbPath); db.Initialize(); return db; }
    private static Site Site() => new() { Name = "通知検査", Url = SecretUrl, NotificationTargetUrl = Destination,
        MonitorMode = MonitorMode.Feed, UpdateDialogNotification = true, PopupNotification = true, WindowsNotification = true };
    private void Sql(string sql) { using var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    [Fact]
    public async Task AllNotificationRoutesUseOneSafeEventSnapshotAndNeverChangeFetchUrl()
    {
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        var fetcher = new Fetcher { Content = Rss("A", "B") };
        var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "logs")));
        Assert.Equal(CheckOutcome.BaselineCreated, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Content = Rss("A", "B changed");
        var result = await engine.CheckAsync(site, default);
        Assert.True(result.ShouldNotify); Assert.Equal(Destination, NotificationUrl.ForEvent(result));
        Assert.All(fetcher.Requested, uri => Assert.Equal(SecretUrl, uri.AbsoluteUri));
        Assert.Equal(SecretUrl, db.GetSite(site.Id)!.Url);
        var pending = db.GetNextPendingUpdateDialog()!;
        Assert.Equal(Destination, pending.Url); Assert.Equal(Destination, Assert.Single(db.GetHistory()).Url);
        var edited = db.GetSite(site.Id)!; edited.NotificationTargetUrl = "https://example.test/later/"; db.SaveSite(edited);
        Assert.Equal(Destination, db.GetNextPendingUpdateDialog()!.Url);
        Assert.Equal(Destination, Assert.Single(db.GetHistory()).Url);
        var xml = global::WebSiteMonitor.NotificationService.CreateToastXml(result.Site, NotificationUrl.ForEvent(result));
        Assert.DoesNotContain("TEST_SECRET", xml); Assert.DoesNotContain("private-feed", xml);
        Assert.Equal(Destination, XDocument.Parse(xml).Root!.Attribute("launch")!.Value);
        var start = global::WebSiteMonitor.BrowserLaunch.CreateStartInfo(pending.Url)!;
        Assert.Equal(Destination, start.FileName); Assert.True(start.UseShellExecute); Assert.Empty(start.Arguments);
        Sta(() =>
        {
            string? popupOpened = null; string? dialogOpened = null;
            using var popup = new global::WebSiteMonitor.UpdatePopup(result.Site, url => popupOpened = url, NotificationUrl.ForEvent(result));
            popup.Show(); Application.DoEvents(); Assert.Equal(Destination, popup.NotificationTargetUrl);
            All(popup).OfType<Button>().Single(button => button.Text == "Webサイトを開く").PerformClick();
            Assert.Equal(Destination, popupOpened);
            using var dialog = new global::WebSiteMonitor.UpdateDialog(pending, () => throw new InvalidOperationException("must not acknowledge"), url => dialogOpened = url);
            dialog.Show(); Application.DoEvents(); dialog.OpenLink();
            Assert.Equal(Destination, dialogOpened); Assert.True(dialog.Visible); Assert.False(dialog.Confirmed);
            Assert.DoesNotContain(All(dialog), control => control.Text.Contains("TEST_SECRET")); dialog.Shutdown();
            string? historyOpened = null;
            using var history = new global::WebSiteMonitor.HistoryForm(db, null, url => historyOpened = url);
            history.Show(); Application.DoEvents();
            var grid = All(history).OfType<DataGridView>().Single(); grid.CurrentCell = grid.Rows[0].Cells[0];
            Assert.Equal(Destination, grid.Rows[0].Cells["Url"].Value);
            All(history).OfType<Button>().Single(button => button.Text == "Webサイトを開く").PerformClick();
            Assert.Equal(Destination, historyOpened); Assert.DoesNotContain(All(history), control => control.Text.Contains("TEST_SECRET"));
            history.Close();
        });
    }

    [Fact]
    public void LegacySchemaSixMigratesWithoutRewritingOldRowsAndRestoresSafeFifo()
    {
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        db.ApplySuccess(site.Id, "A", "A", null, null, MonitorMode.Feed, DateTimeOffset.Now);
        db.ApplySuccess(site.Id, "B", "B", null, null, MonitorMode.Feed, DateTimeOffset.Now);
        var first = db.GetNextPendingUpdateDialog()!;
        db.ApplySuccess(site.Id, "C", "C", null, null, MonitorMode.Feed, DateTimeOffset.Now);
        Sql($"UPDATE PendingUpdateDialogs SET Url='{SecretUrl}'; UPDATE History SET OldPreview='{SecretUrl}',NewPreview='{SecretUrl}'; ALTER TABLE History DROP COLUMN NotificationTargetUrl; UPDATE SchemaVersion SET Version=6;");
        db = new Database(DbPath); db.Initialize();
        Assert.Equal(7, db.GetSchemaVersion()); Assert.NotNull(db.MigrationBackupPath);
        Assert.Equal(first.Id, db.GetNextPendingUpdateDialog()!.Id); Assert.Equal(Destination, db.GetNextPendingUpdateDialog()!.Url);
        Assert.All(db.GetHistory(), entry => { Assert.Equal(Destination, entry.Url); Assert.DoesNotContain("TEST_SECRET", entry.NewPreview); });
        using (var c = new SqliteConnection($"Data Source={db.MigrationBackupPath};Mode=ReadOnly"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Version FROM SchemaVersion"; Assert.Equal(6L, cmd.ExecuteScalar()); }
        db.Initialize(); Assert.Single(Directory.GetFiles(_root, "*.bak"));
        using (var c = new SqliteConnection($"Data Source={DbPath}"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT NewPreview FROM History LIMIT 1"; Assert.Equal(SecretUrl, cmd.ExecuteScalar()); }
        Sta(() =>
        {
            using var controller = new global::WebSiteMonitor.UpdateDialogController(db, new FileLogger(Path.Combine(_root, "logs")), _ => { }, new WindowsFormsSynchronizationContext());
            controller.TryShowNext(); Assert.Equal(Destination, controller.Active!.Notification.Url);
            controller.Dispose();
            using var restored = new global::WebSiteMonitor.UpdateDialogController(new Database(DbPath), new FileLogger(Path.Combine(_root, "logs")), _ => { }, new WindowsFormsSynchronizationContext());
            restored.TryShowNext(); Assert.Equal(first.Id, restored.Active!.Notification.Id);
            Assert.Equal(Destination, restored.Active.Notification.Url);
        });
        Assert.True(db.AcknowledgeUpdateDialog(first.Id)); Assert.True(db.GetNextPendingUpdateDialog()!.Id > first.Id);
        Assert.Equal(2, db.GetHistory().Count);
    }

    [Theory]
    [InlineData("https://example.test/page?authsystem_feed_token_value=ABC123")]
    [InlineData("https://example.test/token-id/ABC123")]
    [InlineData("https://example.test/page?AUTH.SYSTEM.FEED.TOKEN.VALUE=ABC123")]
    [InlineData("https://example.test/page?x-api-key-value=ABC123")]
    [InlineData("https://example.test/page?%2561uth%255ftoken%255fvalue=ABC123")]
    [InlineData("https://example.test/%2574oken-id/ABC123")]
    [InlineData("https://example.test/page#session.value=ABC123")]
    [InlineData("https://example.test/page?q=%2574oken-id%252FABC123")]
    public void DerivedAndEncodedSecretsAreRejectedForExplicitAndAutomaticDestinations(string url)
    {
        Assert.Equal("https://example.test/", NotificationUrl.Sanitize(url));
        Assert.Throws<ArgumentException>(() => NotificationUrl.ValidateExplicit(url));
        Assert.Equal("https://example.test/", global::WebSiteMonitor.BrowserLaunch.CreateStartInfo(url)!.FileName);
    }

    [Fact]
    public void PublicUrlsArePreservedAndMalformedInputsFailClosedWithoutThrowing()
    {
        foreach (var url in new[] { "https://example.test/article?p=42#part", "https://example.test/search?q=audio&page=2" })
        { Assert.Equal(url, NotificationUrl.Sanitize(url)); Assert.Equal(url, NotificationUrl.ValidateExplicit(url)); }
        foreach (var url in new[] { "https://example.test/%ZZ", "https://example.test/%2525252574oken/x", "https://example.test/a\nb", "file:///C:/x", "https://example.test/" + new string('a', 10000) })
        { var safe = NotificationUrl.Sanitize(url); Assert.True(safe.Length == 0 || safe == "https://example.test/"); }
        var invalidSite = Site(); invalidSite.Url = "file:///C:/x"; invalidSite.NotificationTargetUrl = null;
        var toast = XDocument.Parse(global::WebSiteMonitor.NotificationService.CreateToastXml(invalidSite));
        Assert.Null(toast.Root!.Attribute("launch")); Assert.Null(global::WebSiteMonitor.BrowserLaunch.CreateStartInfo(invalidSite.Url));
    }

    [Fact]
    public async Task ErrorsAreRedactedAtLogBoundaryAndInUiModelsAndLoggingFailureDoesNotStopChecks()
    {
        var logs = Path.Combine(_root, "logs"); var logger = new FileLogger(logs);
        logger.Info("request " + SecretUrl);
        logger.Error("authsystem_feed_token_value=TEST_SECRET", new HttpRequestException("403 " + SecretUrl, new Exception(SecretUrl)));
        logger.Error(new Exception("failed " + SecretUrl).ToString());
        var text = string.Join("\n", Directory.GetFiles(logs).Select(File.ReadAllText));
        Assert.DoesNotContain("TEST_SECRET", text); Assert.Contains("403", text); Assert.Contains("HttpRequestException", text); Assert.Contains("[URL_REDACTED]", text);
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        var fetcher = new Fetcher { Failure = new HttpRequestException("403 " + SecretUrl) };
        var engine = new MonitorEngine(db, fetcher, logger);
        var result = await engine.CheckAsync(site, default);
        Assert.Equal(CheckOutcome.Failed, result.Outcome); Assert.DoesNotContain("TEST_SECRET", result.Message);
        Assert.DoesNotContain("TEST_SECRET", db.GetSite(site.Id)!.LastError!);
        var blocked = Path.Combine(_root, "not-directory"); File.WriteAllText(blocked, "test");
        var unavailableLogger = new FileLogger(blocked); unavailableLogger.Error("request " + SecretUrl); unavailableLogger.Cleanup(1);
        var withUnavailableLog = new MonitorEngine(db, fetcher, unavailableLogger);
        Assert.Equal(CheckOutcome.Failed, (await withUnavailableLog.CheckAsync(site, default)).Outcome);
    }

    [Theory]
    [InlineData("second")]
    [InlineData("first")]
    [InlineData("both")]
    [InlineData("reorder")]
    [InlineData("delete")]
    [InlineData("atom")]
    public async Task FeedWithoutEntryBaselinesNeverGuessesAnUnrelatedArticle(string scenario)
    {
        var db = NewDb(); var site = Site(); site.Url = "https://example.test/site/"; site.FeedUrl = SecretUrl; site.NotificationTargetUrl = null; db.SaveSite(site);
        var fetcher = new Fetcher { Content = scenario == "atom" ? Atom("A", "B") : Rss("A", "B") };
        var engine = new MonitorEngine(db, fetcher, new FileLogger(Path.Combine(_root, "logs")));
        await engine.CheckAsync(site, default);
        fetcher.Content = scenario switch
        {
            "first" => Rss("A changed", "B"), "both" => Rss("A changed", "B changed"),
            "reorder" => Rss("A", "B", true), "delete" => "<rss><channel><item><guid>one</guid><title>A</title><link>https://example.test/one</link></item></channel></rss>",
            "atom" => Atom("A", "B changed"), _ => Rss("A", "B changed")
        };
        var result = await engine.CheckAsync(site, default);
        Assert.Equal("https://example.test/site/", NotificationUrl.ForSite(site, fetcher.Content));
        Assert.Equal("https://example.test/site/", NotificationUrl.ForEvent(result));
        Assert.DoesNotContain("/one", NotificationUrl.ForEvent(result));
        site = db.GetSite(site.Id)!; site.NotificationTargetUrl = Destination; db.SaveSite(site);
        Assert.Equal(Destination, NotificationUrl.ForSite(site, fetcher.Content));
    }

    private static string Rss(string first, string second, bool reorder = false)
    {
        var one = $"<item><guid>one</guid><title>{first}</title><link>https://example.test/one</link><description>{first}</description></item>";
        var two = $"<item><guid>two</guid><title>{second}</title><link>https://example.test/two</link><description>{second}</description></item>";
        return "<rss><channel>" + (reorder ? two + one : one + two) + "</channel></rss>";
    }
    private static string Atom(string first, string second) => $"<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>one</id><title>{first}</title><link href='https://example.test/one'/></entry><entry><id>two</id><title>{second}</title><link href='https://example.test/two'/></entry></feed>";
    private static IEnumerable<Control> All(Control parent) { yield return parent; foreach (Control child in parent.Controls) foreach (var nested in All(child)) yield return nested; }
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class Fetcher : IHttpFetcher
    {
        public string Content = "<rss><channel/></rss>"; public Exception? Failure;
        public readonly List<Uri> Requested = [];
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
        {
            Requested.Add(uri); if (Failure is not null) throw Failure;
            return Task.FromResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes(Content), "application/rss+xml", "utf-8", null, null, uri));
        }
    }
}
