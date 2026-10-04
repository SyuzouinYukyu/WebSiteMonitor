using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[CollectionDefinition("V106 Windows Forms", DisableParallelization = true)]
public sealed class V106WindowsFormsCollection;

[Collection("V106 Windows Forms")]
public sealed class V106AuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV106-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "test.db");
    public V106AuditTests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
    private Database NewDb() { var db = new Database(DbPath); db.Initialize(); return db; }
    private static Site Site(string url = "https://example.test/page?p=42") => new()
    { Name = "test", Url = url, UpdateDialogNotification = true, MonitorMode = MonitorMode.Text };
    private static CheckResult Apply(Database db, Site site, string hash, bool master = true)
        => db.ApplySuccess(site.Id, site.MonitorRevision, hash, hash, null, null, MonitorMode.Text,
            DateTimeOffset.Now, dialogNotificationsEnabled: master, notificationUrl: NotificationUrl.ForSite(site));

    [Fact]
    public void SuppressedChangedEventRecordsHistoryButNeverQueuesDialog()
    {
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        Assert.Equal(CheckOutcome.BaselineCreated, Apply(db, site, "A").Outcome);
        var first = Apply(db, site, "B"); Assert.True(first.ShouldNotify);
        var firstPending = db.GetNextPendingUpdateDialog()!; Assert.Equal("https://example.test/page?p=42", firstPending.Url);
        Assert.True(db.MarkNotified(site.Id, site.MonitorRevision, "B"));
        Assert.True(db.AcknowledgeUpdateDialog(firstPending.Id));
        Assert.True(Apply(db, site, "A").ShouldNotify);
        var secondPending = db.GetNextPendingUpdateDialog()!;
        Assert.True(db.AcknowledgeUpdateDialog(secondPending.Id));
        var suppressed = Apply(db, site, "B");
        Assert.Equal(CheckOutcome.Changed, suppressed.Outcome);
        Assert.False(suppressed.ShouldNotify);
        Assert.Null(db.GetNextPendingUpdateDialog());
        Assert.Equal(3, db.GetHistory().Count);
        Assert.Equal(CheckOutcome.Unchanged, Apply(db, site, "B").Outcome);
        Assert.Null(db.GetNextPendingUpdateDialog());
        site = db.GetSite(site.Id)!; site.UpdateDialogNotification = false; db.SaveSite(site);
        Assert.True(Apply(db, site, "C").ShouldNotify);
        Assert.Null(db.GetNextPendingUpdateDialog());
        site = db.GetSite(site.Id)!; site.UpdateDialogNotification = true; db.SaveSite(site);
        Assert.True(Apply(db, site, "D", false).ShouldNotify);
        Assert.Null(db.GetNextPendingUpdateDialog());
        Assert.True(Apply(db, site, "E").ShouldNotify);
        Assert.NotNull(new Database(DbPath).GetNextPendingUpdateDialog());
    }

    [Fact]
    public void V105MigrationPreservesQueueAndSiteAndBacksUpExactlyOnce()
    {
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        Apply(db, site, "A"); Apply(db, site, "B");
        var pending = db.GetNextPendingUpdateDialog()!;
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText =
            "ALTER TABLE Sites DROP COLUMN NotificationTargetUrl; UPDATE SchemaVersion SET Version=5;"; command.ExecuteNonQuery(); }
        db = new Database(DbPath); db.Initialize();
        Assert.Equal(7, db.GetSchemaVersion());
        Assert.Null(db.GetSite(site.Id)!.NotificationTargetUrl);
        Assert.Equal(pending, db.GetNextPendingUpdateDialog());
        Assert.Single(db.GetHistory());
        Assert.NotNull(db.MigrationBackupPath);
        using (var backup = new SqliteConnection($"Data Source={db.MigrationBackupPath};Mode=ReadOnly"))
        { backup.Open(); using var command = backup.CreateCommand(); command.CommandText = "SELECT Version FROM SchemaVersion"; Assert.Equal(5L, command.ExecuteScalar()); }
        db.Initialize(); Assert.Single(Directory.GetFiles(_root, "*.bak"));
        var edited = db.GetSite(site.Id)!; edited.NotificationTargetUrl = "https://example.test/public/?p=42";
        db.SaveSite(edited); Assert.Equal(edited.NotificationTargetUrl, new Database(DbPath).GetSite(site.Id)!.NotificationTargetUrl);
    }

    [Theory]
    [InlineData("https://example.test/article?p=42#part", "https://example.test/article?p=42#part")]
    [InlineData("https://example.test/feed?authsystem_feed_token=PLACEHOLDER", "https://example.test/")]
    [InlineData("https://example.test/token/known-secret", "https://example.test/")]
    [InlineData("https://u:p@example.test/page", "https://example.test/")]
    [InlineData("https://example.test/a/0123456789abcdef0123456789abcdef", "https://example.test/")]
    [InlineData("https://example.test/article#access_token=PLACEHOLDER", "https://example.test/")]
    [InlineData("https://example.test/article?p=0123456789abcdef0123456789abcdef", "https://example.test/")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("https://example.test/page%0aevil", "https://example.test/")]
    public void UrlSafetyPreservesPublicDestinationsAndFallsBackForSecrets(string input, string expected)
        => Assert.Equal(expected, NotificationUrl.Sanitize(input));

    [Fact]
    public void ExplicitDestinationWinsFeedAndIsValidatedOnSave()
    {
        var db = NewDb(); var site = Site("https://example.test/private-feed/?authsystem_feed_token=PLACEHOLDER");
        site.NotificationTargetUrl = "https://example.test/public/?p=42";
        db.SaveSite(site);
        var feed = "<rss><channel><item><link>https://example.test/article?p=7</link></item></channel></rss>";
        Assert.Equal(site.NotificationTargetUrl, NotificationUrl.ForSite(db.GetSite(site.Id)!, feed));
        site.NotificationTargetUrl = null; db.SaveSite(site);
        Assert.Equal("https://example.test/", NotificationUrl.ForSite(site, feed));
        Assert.Equal("https://example.test/", NotificationUrl.ForSite(site));
        foreach (var unsafeUrl in new[] { "file:///C:/x", "https://u:p@example.test/", "https://example.test/?API_KEY=x", "https://example.test/key/x", "https://example.test/a\nb" })
        { site.NotificationTargetUrl = unsafeUrl; Assert.Throws<ArgumentException>(() => db.SaveSite(site)); }
    }

    [Fact]
    public void LegacyPendingUrlIsRecheckedBeforeDisplayAndOpening()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var secret = "https://example.test/token/PLACEHOLDER?auth=x";
                string? opened = null; var acknowledged = false;
                var item = new PendingUpdateDialog(1, 1, DateTimeOffset.Now, secret);
                using var form = new global::WebSiteMonitor.UpdateDialog(item, () => { acknowledged = true; return true; }, url => opened = url);
                form.Show(); Application.DoEvents();
                Assert.Equal("https://example.test/", form.Notification.Url);
                Assert.DoesNotContain("PLACEHOLDER", form.Controls.Cast<Control>().SelectMany(All).Select(control => control.Text).FirstOrDefault(text => text.Contains("https://example.test/")) ?? "");
                form.OpenLink(); Assert.Equal("https://example.test/", opened);
                Assert.False(acknowledged); Assert.True(form.Visible); form.Shutdown();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> All(Control parent)
    { yield return parent; foreach (Control child in parent.Controls) foreach (var nested in All(child)) yield return nested; }
}
