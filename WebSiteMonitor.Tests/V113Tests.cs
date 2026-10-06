using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V113Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitor-v113-tests-" + Guid.NewGuid().ToString("N"));
    public V113Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void TransferMenuFontMatchesParentAndDoesNotAccumulate(int size)
    {
        Sta(() =>
        {
            var settings = new AppSettings { UiFontSize = size };
            global::WebSiteMonitor.UiFontManager.Initialize(settings);
            var db = new Database(Path.Combine(_root, "font.db")); db.Initialize();
            using var http = new SharedHttpFetcher();
            var engine = new MonitorEngine(db, http, new FileLogger(Path.Combine(_root, "logs")));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine);
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.MainForm(db, engine, scheduler, sound, () => settings, _ => { }, () => { }, _ => { });
            form.Show(); Application.DoEvents();
            var menu = All<ToolStrip>(form).SelectMany(s => s.Items.Cast<ToolStripItem>()).OfType<ToolStripDropDownButton>().Single();
            Assert.Equal(4, menu.DropDownItems.Count);
            Assert.Equal(size, menu.Font.SizeInPoints, 1);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                menu.ShowDropDown(); Application.DoEvents();
                foreach (ToolStripItem item in menu.DropDownItems)
                {
                    Assert.True(Math.Abs(item.Font.SizeInPoints - menu.Font.SizeInPoints) < 0.01f,
                        $"parent={menu.Font.SizeInPoints}, child={item.Font.SizeInPoints}: {item.Text}");
                    Assert.Equal(menu.Font.FontFamily.Name, item.Font.FontFamily.Name);
                    Assert.Equal(menu.Font.Style, item.Font.Style);
                }
                menu.HideDropDown(); global::WebSiteMonitor.UiFontManager.Apply(form, size);
            }
            form.AllowExit(); form.Close();
        });
    }

    public static IEnumerable<object[]> LayoutCases()
    {
        foreach (var size in new[] { new Size(1920, 1080), new Size(1366, 768), new Size(1280, 720) })
        foreach (var dpi in new[] { 96, 120, 144 })
        foreach (var font in new[] { 10, 18 })
        foreach (var settings in new[] { false, true })
            yield return [size.Width, size.Height, dpi, font, settings];
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void ScrollBodyAndFixedFooterFitWorkingArea(int width, int height, int dpi, int font, bool isSettings)
    {
        Sta(() =>
        {
            var settings = new AppSettings { UiFontSize = font };
            global::WebSiteMonitor.UiFontManager.Initialize(settings);
            var db = new Database(Path.Combine(_root, "layout.db")); db.Initialize();
            using var http = new SharedHttpFetcher();
            using var sound = new global::WebSiteMonitor.SoundService();
            using Form form = isSettings ? new global::WebSiteMonitor.SettingsForm(settings)
                : new global::WebSiteMonitor.SiteEditForm(new Site { Name = new string('長', 100), Url = "https://example.test/", MonitorMode = MonitorMode.Auto },
                    new MonitorEngine(db, http, new FileLogger(Path.Combine(_root, "logs"))), sound, settings, Path.Combine(_root, "sounds"));
            form.Show(); Application.DoEvents();
            // Supplementary pixel scaling is not an OS DPI change or a visual check.
            var factor = dpi / (float)form.DeviceDpi;
            form.Scale(new SizeF(factor, factor));
            global::WebSiteMonitor.UiFontManager.Apply(form, font);
            var work = new Rectangle(0, 0, width, height - 48); // taskbar excluded
            global::WebSiteMonitor.ScrollableDialogLayout.FitToWorkingArea(form, work);
            Application.DoEvents(); form.PerformLayout();
            Assert.True(work.Contains(form.Bounds), $"form={form.Bounds}, work={work}");
            var viewport = All<Panel>(form).Single(panel => panel.Name == "BodyViewport");
            var body = All<TableLayoutPanel>(form).Single(panel => panel.Name == "ScrollableBody");
            var footer = All<FlowLayoutPanel>(form).Single(panel => panel.Name == "FixedFooter");
            Assert.True(viewport.AutoScroll && viewport.Height > 0);
            Assert.False(footer.AutoScroll);
            var footerBounds = RelativeBounds(form, footer);
            Assert.True(form.ClientRectangle.Contains(footerBounds), $"footer={footerBounds}, client={form.ClientRectangle}");
            Assert.True(RelativeBounds(form, viewport).Bottom <= footerBounds.Top);
            foreach (Button button in footer.Controls)
            {
                var bounds = RelativeBounds(form, button);
                Assert.True(form.ClientRectangle.Contains(bounds), $"button={button.Text}, {bounds}");
                Assert.True(footer.ClientRectangle.Contains(RelativeBounds(footer, button)), button.Text);
                Assert.True(button.Width >= button.GetPreferredSize(Size.Empty).Width, button.Text);
                Assert.True(button.Height >= button.GetPreferredSize(Size.Empty).Height, button.Text);
            }
            if (body.Height > viewport.ClientSize.Height)
            {
                Assert.True(viewport.VerticalScroll.Visible);
                // WinForms ClientSize already excludes the native scrollbar.
                Assert.True(body.Width <= viewport.ClientSize.Width,
                    $"body={body.Width}, viewport={viewport.ClientSize.Width}, scrollbar={SystemInformation.VerticalScrollBarWidth}");
                Assert.True(viewport.VerticalScroll.Maximum >= body.Height - 1);
            }
            Control last = isSettings ? All<Button>(body).Single(button => button.Text == "Windows統合情報を解除")
                : All<TextBox>(body).Single(box => box.Multiline);
            viewport.ScrollControlIntoView(last); Application.DoEvents();
            Assert.Equal(footerBounds, RelativeBounds(form, footer));
            Assert.True(RelativeBounds(viewport, last).Bottom <= viewport.ClientRectangle.Bottom,
                $"last={RelativeBounds(viewport, last)}, viewport={viewport.ClientRectangle}, scroll={viewport.AutoScrollPosition}, body={body.Bounds}, max={viewport.VerticalScroll.Maximum}");
            if (!isSettings)
            {
                var test = All<Button>(body).Single(button => button.Text == "監視テスト / 抽出プレビュー");
                viewport.ScrollControlIntoView(test); Application.DoEvents();
                Assert.True(viewport.ClientRectangle.Contains(RelativeBounds(viewport, test)));
                Assert.Equal(3, All<Button>(body).Count(button => button.Text == "貼り付け" && button.Visible));
                Assert.All(All<TextBox>(body).Where(box => !box.Multiline), box => Assert.True(box.Width >= 60, $"input width={box.Width}"));
            }
            form.Close();
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    public void BoundsClampPreservesAdequateSizeAndSupportsNegativeMonitorOrigins(int dpi)
    {
        var work = new Rectangle(-1366, 30, 1366, 690);
        var result = global::WebSiteMonitor.ScrollableDialogLayout.ConstrainBounds(new Rectangle(0, -500, 3000, 2000), work, new Size(860, 320), dpi);
        Assert.Equal(work, result);
        var adequate = new Rectangle(-1300, 50, 1100, 600);
        var clamped = global::WebSiteMonitor.ScrollableDialogLayout.ConstrainBounds(adequate, new Rectangle(-1920, 0, 1920, 1080), new Size(680, 300), dpi);
        Assert.Equal(adequate, clamped);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("escape")]
    [InlineData("caption")]
    [InlineData("enter")]
    [InlineData("programmatic")]
    public void ClosingOnlyAcknowledgesCurrentAndAdvancesFifoOnce(string gesture)
    {
        Sta(() =>
        {
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = 18 });
            var db = new Database(Path.Combine(_root, "fifo.db")); db.Initialize();
            var site = new Site { Name = "通知対象", Url = "https://monitor.test/", NotificationTargetUrl = "https://destination.test/page", MonitorMode = MonitorMode.Text, UpdateDialogNotification = true };
            db.SaveSite(site);
            foreach (var hash in new[] { "A", "B", "C", "D" }) db.ApplySuccess(site.Id, hash, hash, null, null, MonitorMode.Text, DateTimeOffset.Now);
            var history = db.GetHistory().Count; var firstPending = db.GetNextPendingUpdateDialog()!;
            var ui = new QueuedContext(); string? opened = null;
            using var controller = new global::WebSiteMonitor.UpdateDialogController(db, new FileLogger(Path.Combine(_root, "logs")), url => opened = url, ui);
            controller.TryShowNext(); var first = controller.Active!; Application.DoEvents();
            All<Button>(first).Single(button => button.Text == "Webサイトを見る").PerformClick();
            Assert.Equal("https://destination.test/page", opened);
            Assert.Equal(firstPending, db.GetNextPendingUpdateDialog()); Assert.False(first.Confirmed);
            controller.TryShowNext(); Assert.Same(first, controller.Active);
            if (gesture == "button") All<Button>(first).Single(button => button.Text == "閉じる").PerformClick();
            else if (gesture is "escape" or "enter")
            {
                if (gesture == "enter") first.ActiveControl = All<Button>(first).Single(button => button.Text == "閉じる");
                Key(first, gesture == "escape" ? Keys.Escape : Keys.Enter);
            }
            else if (gesture == "caption") SendMessage(first.Handle, 0x0112, (IntPtr)0xF060, IntPtr.Zero);
            else first.Close();
            Application.DoEvents();
            Assert.True(first.Confirmed); Assert.Null(controller.Active); Assert.Single(ui.Posts);
            var secondPending = db.GetNextPendingUpdateDialog()!; Assert.True(secondPending.Id > firstPending.Id);
            ui.Drain(); var second = controller.Active!;
            Assert.Equal(secondPending, second.Notification); controller.TryShowNext(); Assert.Same(second, controller.Active);
            Assert.Equal(history, db.GetHistory().Count);
            controller.Dispose(); Assert.False(second.Confirmed); Assert.Equal(secondPending, db.GetNextPendingUpdateDialog());
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void LongNotificationUrlNeverOverlapsFooterAndFailuresRemainUnconfirmed(int font)
    {
        Sta(() =>
        {
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = font });
            var acknowledgements = 0;
            using var form = new global::WebSiteMonitor.UpdateDialog(new PendingUpdateDialog(1, 1, DateTimeOffset.Now, "https://example.test/" + new string('a', 4000)),
                () => { acknowledgements++; return false; }, _ => throw new IOException("token=secret"));
            form.Show(); Application.DoEvents();
            var browse = All<Button>(form).Single(button => button.Text == "Webサイトを見る");
            var close = All<Button>(form).Single(button => button.Text == "閉じる");
            var scroll = All<Panel>(form).Single(panel => panel.AutoScroll);
            browse.PerformClick(); Application.DoEvents(); Assert.Equal(0, acknowledgements);
            Assert.Contains(All<Label>(form), label => label.Visible && label.Text == "ブラウザーを開けませんでした。");
            close.PerformClick(); Application.DoEvents(); Assert.True(form.Visible); Assert.False(form.Confirmed); Assert.Equal(1, acknowledgements);
            foreach (var factor in new[] { 1f, 1.25f, 1.5f })
            {
                form.Scale(new SizeF(factor, factor)); Application.DoEvents();
                foreach (var button in new[] { close, browse }) Assert.True(form.ClientRectangle.Contains(RelativeBounds(form, button)), button.Text);
                Assert.True(RelativeBounds(form, scroll).Bottom <= RelativeBounds(form, close.Parent!).Top);
            }
            Assert.DoesNotContain(All<Label>(form), label => label.Text.Contains("secret"));
            form.Shutdown(); Assert.Equal(1, acknowledgements);
            using var invalid = new global::WebSiteMonitor.UpdateDialog(new PendingUpdateDialog(2, 1, DateTimeOffset.Now, "file:///C:/danger.exe"), () => true, _ => throw new Exception());
            Assert.False(All<Button>(invalid).Single(button => button.Text == "Webサイトを見る").Enabled);
            invalid.OpenLink(); Assert.False(invalid.Confirmed); invalid.Shutdown();
        });
    }

    [Fact]
    public void LatestLogSelectionUsesUtcMetadataCaseInsensitiveExtensionAndStableTie()
    {
        var directory = Path.Combine(_root, "日本語 空白", "logs"); Directory.CreateDirectory(directory);
        var older = Log(directory, "old.log", -2); var newest = Log(directory, "a.LOG", 0);
        Log(directory, "b.log", 0); Log(directory, "newer.txt", 1); Log(directory, "compressed.log.gz", 2);
        var child = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName; Log(child, "newest.log", 3);
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        ProcessStartInfo? received = null; var messages = new List<string>();
        global::WebSiteMonitor.LogFileLaunch.OpenLatest(directory, messages.Add, info => { received = info; return null; });
        Assert.Empty(messages); Assert.NotNull(received); Assert.Equal(newest, received.FileName);
        Assert.True(received.UseShellExecute); Assert.Empty(received.Arguments); Assert.Empty(received.ArgumentList);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))));
        File.SetLastWriteTimeUtc(older, DateTime.UnixEpoch.AddDays(10));
        Assert.Equal(older, global::WebSiteMonitor.LogFileLaunch.FindLatest(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrIneligibleLogsDoNotCreateDirectoriesOrLaunch(bool existing)
    {
        var directory = Path.Combine(_root, "logs");
        if (existing) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "app.txt"), "not a log"); }
        var launches = 0; var messages = new List<string>();
        global::WebSiteMonitor.LogFileLaunch.OpenLatest(directory, messages.Add, _ => { launches++; return null; });
        Assert.Equal(0, launches); Assert.Equal([global::WebSiteMonitor.LogFileLaunch.NoLog], messages);
        Assert.Equal(existing, Directory.Exists(directory));
    }

    [Theory]
    [InlineData("access")]
    [InlineData("association")]
    [InlineData("disappeared")]
    public void LogLaunchErrorsOnlyShowFixedJapaneseMessage(string error)
    {
        var path = Log(_root, "test.log", 0); var messages = new List<string>();
        global::WebSiteMonitor.LogFileLaunch.OpenLatest(_root, messages.Add, _ =>
        {
            if (error == "disappeared") { File.Delete(path); throw new FileNotFoundException("token=secret"); }
            if (error == "access") throw new UnauthorizedAccessException("token=secret");
            throw new System.ComponentModel.Win32Exception("token=secret");
        });
        Assert.Equal([global::WebSiteMonitor.LogFileLaunch.Failed], messages);
    }

    [Fact]
    public void LogDirectoryAccessFailureIsNotReportedAsEmpty()
    {
        var messages = new List<string>();
        global::WebSiteMonitor.LogFileLaunch.OpenLatest("invalid\0directory", messages.Add, _ => throw new Exception());
        Assert.Equal([global::WebSiteMonitor.LogFileLaunch.Failed], messages);
    }

    [Fact]
    public void SettingsCancelPreservesValuesAndSaveStillUsesExistingControls()
    {
        Sta(() =>
        {
            var settings = new AppSettings { HistoryRetentionDays = 100, LogRetentionDays = 50, PcmSampleRate = 48000, PcmBits = 24, PcmChannels = 1 };
            using (var cancel = new global::WebSiteMonitor.SettingsForm(settings))
            {
                cancel.Show(); All<NumericUpDown>(cancel).First().Value = 200;
                All<Button>(cancel).Single(button => button.Text == "キャンセル").PerformClick(); cancel.Close();
            }
            Assert.Equal(100, settings.HistoryRetentionDays); Assert.Equal(50, settings.LogRetentionDays);
            using var save = new global::WebSiteMonitor.SettingsForm(settings); save.Show();
            var history = (NumericUpDown)typeof(global::WebSiteMonitor.SettingsForm).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save)!;
            history.Value = 250; All<Button>(save).Single(button => button.Text == "保存").PerformClick();
            Assert.Equal(DialogResult.OK, save.DialogResult); Assert.Equal(250, settings.HistoryRetentionDays);
            Assert.Equal(48000, settings.PcmSampleRate); Assert.Equal(24, settings.PcmBits); Assert.Equal(1, settings.PcmChannels);
        });
    }

    [Fact]
    public void ToolbarLogIsBetweenHistoryAndSettingsAndUsesInjectedActualPathAction()
    {
        Sta(() =>
        {
            var db = new Database(Path.Combine(_root, "toolbar.db")); db.Initialize();
            using var http = new SharedHttpFetcher(); var engine = new MonitorEngine(db, http, new FileLogger(Path.Combine(_root, "logs")));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine); using var sound = new global::WebSiteMonitor.SoundService();
            var calls = 0;
            using var form = new global::WebSiteMonitor.MainForm(db, engine, scheduler, sound, () => new AppSettings(), _ => { }, () => { }, _ => { }, showLog: () => calls++);
            var toolbar = All<ToolStrip>(form).Single(strip => strip is not StatusStrip);
            var index = toolbar.Items.Cast<ToolStripItem>().Select((item, i) => (item, i)).Single(pair => pair.item.Text == "ログ").i;
            Assert.Equal("更新履歴", toolbar.Items[index - 1].Text); Assert.Equal("設定", toolbar.Items[index + 1].Text);
            toolbar.Items[index].PerformClick(); Assert.Equal(1, calls);
            Assert.All(toolbar.Items.Cast<ToolStripItem>().Where(item => item.Text is "再起動" or "完全に閉じる"),
                item => { Assert.Equal(ToolStripItemAlignment.Right, item.Alignment); Assert.Equal(ToolStripItemOverflow.Never, item.Overflow); });
        });
    }

    private string Log(string directory, string name, int day)
    {
        var path = Path.GetFullPath(Path.Combine(directory, name)); File.WriteAllText(path, "test-owned content");
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(5 + day)); return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SiteEditorPreservesModesValuesAndSaveCancelSemantics(bool saveChanges)
    {
        Sta(() =>
        {
            var settings = new AppSettings { UiFontSize = 18 }; var db = new Database(Path.Combine(_root, "edit.db")); db.Initialize();
            var original = new Site { Name = "変更前", Url = "https://example.test/", NotificationTargetUrl = "https://destination.test/", MonitorMode = MonitorMode.Text,
                ScheduleMode = ScheduleMode.Manual, SoundVolume = 75, UpdateDialogNotification = false, Selector = "article", XPath = "//article", Regex = "article", FeedUrl = "https://example.test/feed" };
            db.SaveSite(original);
            using var http = new SharedHttpFetcher(); using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.SiteEditForm(original, new MonitorEngine(db, http, new FileLogger(Path.Combine(_root, "logs"))), sound, settings, Path.Combine(_root, "sounds"));
            form.Show(); Application.DoEvents(); global::WebSiteMonitor.ScrollableDialogLayout.FitToWorkingArea(form, new Rectangle(0, 0, 1280, 672));
            var mode = Field<ComboBox>(form, "_mode");
            for (var index = 0; index < mode.Items.Count; index++)
            {
                mode.SelectedIndex = index; Application.DoEvents();
                if (index is 0) Assert.True(Field<Label>(form, "_autoHint").Visible);
                if (index is 1) Assert.True(Field<TextBox>(form, "_feed").Visible);
                if (index is 4) Assert.True(Field<TextBox>(form, "_selector").Visible);
                if (index is 5) Assert.True(Field<TextBox>(form, "_xpath").Visible);
                if (index is 6) Assert.True(Field<TextBox>(form, "_regex").Visible);
            }
            mode.SelectedIndex = 3; Field<TextBox>(form, "_name").Text = "変更後";
            var foundFooterFocus = false;
            for (var tab = 0; tab < 70; tab++)
            {
                form.SelectNextControl(form.ActiveControl, true, true, true, true);
                foundFooterFocus |= All<Button>(form).Any(button => button.Text == "保存" && button.Focused);
            }
            Assert.True(foundFooterFocus, "Save must be reachable through normal Tab navigation");
            All<Button>(form).Single(button => button.Text == (saveChanges ? "保存" : "キャンセル")).PerformClick();
            if (saveChanges)
            {
                Assert.Equal(DialogResult.OK, form.DialogResult); Assert.Equal("変更後", form.Value.Name);
                Assert.Equal("https://destination.test/", form.Value.NotificationTargetUrl);
                Assert.Equal(75, form.Value.SoundVolume); Assert.False(form.Value.UpdateDialogNotification);
                Assert.Equal(ScheduleMode.Manual, form.Value.ScheduleMode);
            }
            else Assert.Equal("変更前", form.Value.Name);
            Assert.Equal("変更前", original.Name); Assert.Equal("変更前", db.GetSite(original.Id)!.Name); form.Close();
        });
    }

    private static T Field<T>(object target, string field)
        => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Rectangle RelativeBounds(Control parent, Control control)
        => parent.RectangleToClient(control.Parent!.RectangleToScreen(control.Bounds));
    private static void Key(Form form, Keys keys)
    {
        var msg = Message.Create(form.Handle, 0x0100, IntPtr.Zero, IntPtr.Zero);
        var handled = (bool)typeof(global::WebSiteMonitor.UpdateDialog).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [msg, keys])!;
        if (!handled) typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [keys]);
    }

    [Fact]
    public void EnterOnFocusedBrowserButtonOpensWithoutAcknowledging()
    {
        Sta(() =>
        {
            var acknowledgements = 0; string? opened = null;
            using var form = new global::WebSiteMonitor.UpdateDialog(new PendingUpdateDialog(1, 1, DateTimeOffset.Now, "https://destination.test/"),
                () => { acknowledgements++; return true; }, url => opened = url);
            form.Show(); Application.DoEvents();
            form.ActiveControl = All<Button>(form).Single(button => button.Text == "Webサイトを見る");
            Key(form, Keys.Enter); Application.DoEvents();
            Assert.False(form.Confirmed); Assert.Equal(0, acknowledgements); Assert.True(form.Visible);
            Assert.Equal("https://destination.test/", opened); form.Shutdown();
        });
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private sealed class QueuedContext : SynchronizationContext
    {
        internal List<Action> Posts { get; } = [];
        public override void Post(SendOrPostCallback action, object? state) => Posts.Add(() => action(state));
        internal void Drain() { foreach (var action in Posts.ToArray()) { Posts.Remove(action); action(); } }
    }

    private static IEnumerable<T> All<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in All<T>(child)) yield return nested;
        }
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "STA test timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
