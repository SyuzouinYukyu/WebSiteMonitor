using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V105DialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV105-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_root, name);
    public V105DialogTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }
    private Database NewDb(string name = "test.db") { var db = new Database(PathFor(name)); db.Initialize(); return db; }
    private static Site Site(bool dialog = false, string url = "https://example.test/page") => new()
    { Name = "通知検査", Url = url, UpdateDialogNotification = dialog, MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" };
    private void Sql(string text, string name = "test.db")
    {
        using var c = new SqliteConnection($"Data Source={PathFor(name)}"); c.Open();
        using var command = c.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
    }
    private static CheckResult Apply(Database db, Site site, string hash)
        => db.ApplySuccess(site.Id, hash, hash, null, null, MonitorMode.Text, DateTimeOffset.Now);

    [Fact]
    public void SchemaFourMigrationPreservesDataAndCreatesRestorableBackupOnlyOnce()
    {
        var db = NewDb(); var site = Site();
        site.PopupNotification = true; site.SoundNotification = true; site.UseBrowserCompatibleUserAgent = true;
        db.SaveSite(site); Apply(db, site, "baseline"); Apply(db, site, "changed");
        Sql("ALTER TABLE Sites DROP COLUMN UpdateDialogNotification; DROP TABLE PendingUpdateDialogs; UPDATE SchemaVersion SET Version=4;");
        db = new Database(PathFor("test.db")); db.Initialize();
        Assert.Equal(7, db.GetSchemaVersion());
        var restored = db.GetSite(site.Id)!;
        Assert.False(restored.UpdateDialogNotification);
        Assert.True(restored.WindowsNotification && restored.PopupNotification && restored.SoundNotification && restored.UseBrowserCompatibleUserAgent);
        Assert.Equal("changed", restored.LastHash); Assert.Single(db.GetHistory());
        Assert.NotNull(db.MigrationBackupPath); Assert.True(File.Exists(db.MigrationBackupPath));
        using (var backup = new SqliteConnection($"Data Source={db.MigrationBackupPath};Mode=ReadOnly"))
        {
            backup.Open(); using var command = backup.CreateCommand(); command.CommandText = "SELECT Version FROM SchemaVersion";
            Assert.Equal(4L, command.ExecuteScalar());
        }
        db.Initialize(); Assert.Single(Directory.GetFiles(_root, "*.bak"));
        restored.UpdateDialogNotification = true; db.SaveSite(restored);
        Assert.True(new Database(PathFor("test.db")).GetSite(site.Id)!.UpdateDialogNotification);
        Assert.False(Site().UpdateDialogNotification);
    }

    [Fact]
    public void DurableFifoKeepsDistinctUpdatesAndSurvivesDisabledAndDeletedSites()
    {
        var db = NewDb(); var first = Site(true, "https://example.test/one?token=secret"); var second = Site(true, "https://example.test/two");
        db.SaveSite(first); db.SaveSite(second);
        Apply(db, first, "A"); Apply(db, second, "A"); Assert.Null(db.GetNextPendingUpdateDialog());
        Apply(db, first, "B"); var pending = db.GetNextPendingUpdateDialog()!;
        Apply(db, first, "B"); Apply(db, second, "B"); Apply(db, first, "A");
        var saved = db.GetSite(first.Id)!; saved.UpdateDialogNotification = false; db.SaveSite(saved); Apply(db, first, "C");
        db.DeleteSite(first.Id);
        db = new Database(PathFor("test.db")); db.Initialize();
        Assert.Equal(pending, db.GetNextPendingUpdateDialog()); Assert.Equal("https://example.test/", pending.Url);
        Assert.True(db.AcknowledgeUpdateDialog(pending.Id));
        var next = db.GetNextPendingUpdateDialog()!; Assert.Equal(second.Id, next.SiteId);
        var historyCount = db.GetHistory().Count; Assert.True(db.AcknowledgeUpdateDialog(next.Id)); Assert.Equal(historyCount, db.GetHistory().Count);
        next = db.GetNextPendingUpdateDialog()!; Assert.Equal(first.Id, next.SiteId);
        Assert.True(db.AcknowledgeUpdateDialog(next.Id)); Assert.Null(db.GetNextPendingUpdateDialog());
    }

    [Fact]
    public async Task EngineOnlyQueuesOptedInChangesAndRespectsMasterAndFailures()
    {
        var db = NewDb(); var site = Site(); db.SaveSite(site);
        var fetcher = new TestFetcher(); var enabled = true;
        var engine = new MonitorEngine(db, fetcher, new FileLogger(PathFor("logs")), () => enabled);
        Assert.Equal(CheckOutcome.BaselineCreated, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Content = "B"; await engine.CheckAsync(site, default); Assert.Null(db.GetNextPendingUpdateDialog());
        site = db.GetSite(site.Id)!; site.UpdateDialogNotification = true; db.SaveSite(site);
        await engine.CheckAsync(site, default); Assert.Null(db.GetNextPendingUpdateDialog());
        enabled = false; fetcher.Content = "C"; await engine.CheckAsync(site, default); Assert.Null(db.GetNextPendingUpdateDialog());
        enabled = true; fetcher.Fail = true; Assert.Equal(CheckOutcome.Failed, (await engine.CheckAsync(site, default)).Outcome); Assert.Null(db.GetNextPendingUpdateDialog());
        fetcher.Fail = false; fetcher.Content = "D"; await engine.CheckAsync(site, default);
        Assert.NotNull(db.GetNextPendingUpdateDialog());
    }

    [Fact]
    public async Task QueueWriteFailureRollsBackChangeAndLaterChecksRecover()
    {
        var db = NewDb(); var site = Site(true); db.SaveSite(site);
        var fetcher = new TestFetcher(); var engine = new MonitorEngine(db, fetcher, new FileLogger(PathFor("logs")));
        await engine.CheckAsync(site, default); var baseline = db.GetSite(site.Id)!.LastHash;
        Sql("CREATE TRIGGER RejectDialog BEFORE INSERT ON PendingUpdateDialogs BEGIN SELECT RAISE(FAIL,'queue storage unavailable'); END;");
        fetcher.Content = "B"; Assert.Equal(CheckOutcome.Failed, (await engine.CheckAsync(site, default)).Outcome);
        Assert.Equal(baseline, db.GetSite(site.Id)!.LastHash); Assert.Empty(db.GetHistory()); Assert.Null(db.GetNextPendingUpdateDialog());
        Sql("DROP TRIGGER RejectDialog;"); Assert.Equal(CheckOutcome.Changed, (await engine.CheckAsync(site, default)).Outcome);
        Assert.Single(db.GetHistory()); Assert.NotNull(db.GetNextPendingUpdateDialog());
    }

    [Fact]
    public void NotificationUrlsRemoveSecretsAndOnlyUseSameOriginFeedPages()
    {
        var site = Site(true, "https://user:password@example.test/feed?token=secret#private");
        Assert.Equal("https://example.test/", NotificationUrl.ForSite(site));
        var xml = "<rss><channel><item><link>https://other.test/leak?token=secret</link></item><item><link>https://example.test/article?q=secret</link></item></channel></rss>";
        Assert.Equal("https://example.test/", NotificationUrl.ForSite(site, xml));
        Assert.Equal("https://example.test/", NotificationUrl.ForSite(site, "<!DOCTYPE rss SYSTEM 'file:///secret'><rss/>"));
        Assert.Equal("", NotificationUrl.Sanitize("file:///C:/secret"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(18)]
    public void DialogStaysVisibleForLinksFocusAndShutdownWithoutAcknowledging(int fontSize)
    {
        Sta(() =>
        {
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = fontSize });
            var acknowledgements = 0; string? opened = null;
            var notification = new PendingUpdateDialog(1, 1, DateTimeOffset.Now, "https://example.test/" + new string('a', 4000));
            using var form = new global::WebSiteMonitor.UpdateDialog(notification, () => { acknowledgements++; return true; }, value => opened = value);
            form.Show(); Application.DoEvents();
            Assert.InRange(form.Font.SizeInPoints, fontSize - 0.1f, fontSize + 0.1f);
            Assert.True(form.TopMost && form.ShowInTaskbar); Assert.False(form.MinimizeBox || form.MaximizeBox);
            Assert.Equal("WebSite Monitor — 更新通知", form.Text);
            Assert.Equal(new[] { "閉じる", "Webサイトを見る" }, All<Button>(form).Select(button => button.Text));
            form.OpenLink(); Assert.Equal(notification.Url, opened); Assert.True(form.Visible); Assert.Equal(0, acknowledgements);
            Assert.True(form.Visible); Assert.Equal(0, acknowledgements);
            using (var other = new Form()) { other.Show(); other.Activate(); Application.DoEvents(); }
            Assert.True(form.Visible); Assert.False(form.Confirmed);
            var shutdown = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
            typeof(global::WebSiteMonitor.UpdateDialog).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [shutdown]);
            Assert.False(shutdown.Cancel); Assert.False(form.Confirmed); Assert.Equal(0, acknowledgements);
            Assert.Single(All<LinkLabel>(form)); Assert.Contains(All<Panel>(form), panel => panel.AutoScroll);
            var message = All<Label>(form).Single(label => label.Text == "ウェブサイトが更新されました。");
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            {
                using var font = new Font(message.Font.FontFamily, message.Font.SizeInPoints * scale);
                var width = TextRenderer.MeasureText(message.Text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                Assert.True(message.Width * scale >= width);
            }
            form.Shutdown(); Application.DoEvents(); Assert.False(form.Confirmed); Assert.Equal(0, acknowledgements);
        });
    }

    [Fact]
    public void FailedAcknowledgementKeepsDialogAndConfirmedCaptionCloseAdvancesFifo()
    {
        Sta(() =>
        {
            var db = NewDb(); var site = Site(true); db.SaveSite(site); Apply(db, site, "A"); Apply(db, site, "B"); Apply(db, site, "C");
            using var controller = new global::WebSiteMonitor.UpdateDialogController(db, new FileLogger(PathFor("logs")), _ => { }, new WindowsFormsSynchronizationContext());
            var pendingBeforeTransfer = db.GetNextPendingUpdateDialog();
            controller.ConfigurationSuspended = true;
            controller.TryShowNext(); Assert.Null(controller.Active);
            Assert.Equal(pendingBeforeTransfer, db.GetNextPendingUpdateDialog());
            controller.ConfigurationSuspended = false;
            controller.TryShowNext(); var first = controller.Active!; var id = first.Notification.Id;
            Apply(db, site, "D");
            controller.TryShowNext(); Assert.Same(first, controller.Active);
            using (var failing = new global::WebSiteMonitor.UpdateDialog(first.Notification, () => false, _ => throw new InvalidOperationException()))
            { failing.Show(); failing.OpenLink(); Assert.False(failing.ConfirmFromCaption()); failing.Close(); Assert.True(failing.Visible); failing.Shutdown(); }
            controller.ConfigurationSuspended = true;
            Assert.True(first.ConfirmFromCaption()); first.Close(); Application.DoEvents();
            Assert.Null(controller.Active); Assert.NotNull(db.GetNextPendingUpdateDialog());
            controller.ConfigurationSuspended = false; controller.TryShowNext();
            Assert.NotNull(controller.Active); Assert.True(controller.Active!.Notification.Id > id);
            var unconfirmed = controller.Active.Notification; controller.Dispose();
            Assert.Equal(unconfirmed, db.GetNextPendingUpdateDialog());
            using var restarted = new global::WebSiteMonitor.UpdateDialogController(new Database(PathFor("test.db")), new FileLogger(PathFor("logs")), _ => { }, new WindowsFormsSynchronizationContext());
            restarted.TryShowNext(); Assert.Equal(unconfirmed, restarted.Active!.Notification);
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(18)]
    public void EditorNotificationOrderAndSavedStateAreIndependentAtSupportedFonts(int fontSize)
    {
        Sta(() =>
        {
            var db = NewDb(); var site = Site(true); db.SaveSite(site);
            using var sound = new global::WebSiteMonitor.SoundService();
            var engine = new MonitorEngine(db, new TestFetcher(), new FileLogger(PathFor("logs")));
            using var editor = new global::WebSiteMonitor.SiteEditForm(db.GetSite(site.Id), engine, sound, new AppSettings { UiFontSize = fontSize }, PathFor("sounds"));
            editor.Show(); Application.DoEvents();
            var group = All<GroupBox>(editor).Single(g => g.Text == "更新を検出したときの通知");
            var boxes = All<CheckBox>(group).ToArray();
            Assert.Equal(new[] { "Windows通知", "独自ポップアップ", "更新ダイアログ", "通知サウンド" }, boxes.Select(b => b.Text));
            Assert.True(boxes[0].Checked && boxes[2].Checked); Assert.False(boxes[1].Checked || boxes[3].Checked);
            boxes[1].Checked = true; boxes[3].Checked = true;
            var edited = (Site)typeof(global::WebSiteMonitor.SiteEditForm).GetMethod("ReadValues", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
            db.SaveSite(edited); Assert.True(new Database(PathFor("test.db")).GetSite(site.Id)!.UpdateDialogNotification);
            foreach (var box in boxes)
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            {
                using var font = new Font(box.Font.FontFamily, box.Font.SizeInPoints * scale);
                var width = TextRenderer.MeasureText(box.Text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                Assert.True(box.Width * scale >= width + 16);
            }
            editor.Close();
        });
    }

    private static IEnumerable<T> All<T>(Control parent) where T : Control
    { foreach (Control child in parent.Controls) { if (child is T match) yield return match; foreach (var descendant in All<T>(child)) yield return descendant; } }
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
    private sealed class TestFetcher : IHttpFetcher
    {
        public string Content = "A"; public bool Fail;
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
        {
            if (Fail) throw new HttpRequestException("test failure");
            return Task.FromResult(new HttpFetchResult(200, Encoding.UTF8.GetBytes("<html>" + Content + "</html>"), "text/html", "utf-8", null, null, uri));
        }
    }
}
