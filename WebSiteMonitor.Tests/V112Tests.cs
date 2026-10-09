using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V112Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorV112-" + Guid.NewGuid().ToString("N"));
    private Database Db() { var db = new Database(Path.Combine(_root, "test.db")); db.Initialize(); return db; }
    private static Site Site(string name, string url) => new() { Name = name, Url = url, MonitorMode = MonitorMode.Text, ScheduleMode = ScheduleMode.Manual };
    private FileLogger Logger() => new(Path.Combine(_root, "logs"));
    private string Logs() => string.Join("\n", Directory.GetFiles(Path.Combine(_root, "logs"), "*.log").Select(File.ReadAllText));

    [Theory]
    [InlineData("[DIAGNOSTIC_REDACTED]", "安全上の理由により内容を非表示")]
    [InlineData("[URL_REDACTED]", "URLを非表示")]
    [InlineData("[SECRET_REDACTED]", "機密情報を非表示")]
    [InlineData("[CREDENTIAL_REDACTED]", "認証情報を非表示")]
    public void DisplayMarkersAreJapaneseWithoutChangingInternalRedaction(string marker, string expected)
    {
        Assert.Equal(expected, global::WebSiteMonitor.DisplayText.Content(marker));
        Assert.Equal(marker, NotificationUrl.RedactText(marker));
        const string original = "Timeout Forbidden Canceled Not Found 記事本文";
        Assert.Equal(original, global::WebSiteMonitor.DisplayText.Content(original));
        Assert.Equal(ContentHasher.Sha256(original), ContentHasher.Sha256(global::WebSiteMonitor.DisplayText.Content(original)));
        Assert.DoesNotContain("super-secret", global::WebSiteMonitor.DisplayText.Content("password=super-secret Bearer private-value"));
    }

    [Theory]
    [InlineData(401, "認証が必要です")]
    [InlineData(403, "アクセスが拒否されました")]
    [InlineData(404, "対象が見つかりません")]
    [InlineData(429, "アクセス回数が制限されています")]
    public void HttpDiagnosticsKeepNumberButNeverExternalExceptionBody(int code, string japanese)
    {
        var text = global::WebSiteMonitor.DisplayText.Exception(new HttpRequestException("unsafe-value token=secret", null, (System.Net.HttpStatusCode)code));
        Assert.Contains($"HTTP {code}：{japanese}", text);
        Assert.DoesNotContain("Exception", text); Assert.DoesNotContain("REDACTED", text);
        Assert.DoesNotContain("unsafe-value", text); Assert.DoesNotContain("secret", text);
        Assert.Contains("通信がタイムアウトしました", global::WebSiteMonitor.DisplayText.Exception(new TimeoutException("external")));
        Assert.Contains("処理を中止しました", global::WebSiteMonitor.DisplayText.Exception(new OperationCanceledException("external")));
    }

    [Fact]
    public void SortedGridUsesStableIdsForEveryActionAndKeepsSelectionAndWidths()
    {
        RunSta(() =>
        {
            var db = Db(); var alpha = Site("Alpha", "https://a.test/"); var zulu = Site("Zulu", "https://z.test/"); db.SaveSite(alpha); db.SaveSite(zulu);
            var fetcher = new MutableFetcher(); var engine = new MonitorEngine(db, fetcher, Logger());
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine); using var sound = new global::WebSiteMonitor.SoundService();
            var settings = new AppSettings(); string? opened = null;
            using var main = new global::WebSiteMonitor.MainForm(db, engine, scheduler, sound, () => settings, _ => { }, () => { }, url => opened = url);
            main.Show(); Application.DoEvents(); var grid = Get<DataGridView>(main, "_grid");
            grid.Sort(grid.Columns["Name"]!, ListSortDirection.Descending); grid.CurrentCell = grid.Rows[0].Cells[1];
            Assert.Equal(zulu.Id, grid.CurrentRow!.Tag); Assert.Equal(zulu.Id, ((Site)Invoke(main, "Selected")!).Id);
            grid.Columns["Name"]!.Width = 237; main.Reload(); Assert.Equal(zulu.Id, grid.CurrentRow!.Tag); Assert.Equal(237, grid.Columns["Name"]!.Width); Assert.Equal(SortOrder.Descending, grid.SortOrder);
            var menu = grid.ContextMenuStrip!;
            Item(menu, "Webサイトを開く").PerformClick(); Assert.Equal(zulu.Url, opened);
            Item(menu, "有効 / 無効").PerformClick(); Assert.False(db.GetSite(zulu.Id)!.Enabled); Assert.True(db.GetSite(alpha.Id)!.Enabled);
            Item(menu, "今すぐ確認").PerformClick(); PumpUntil(() => db.GetSite(zulu.Id)!.LastChecked is not null);
            Assert.Equal(new Uri(zulu.Url), fetcher.Requests.Single()); Assert.Null(db.GetSite(alpha.Id)!.LastChecked);
            ObserveModal<global::WebSiteMonitor.SiteEditForm>(() => Item(menu, "編集").PerformClick(), edit => Assert.Equal(zulu.Id, edit.Value.Id));
            ObserveModal<global::WebSiteMonitor.SiteEditForm>(() => Invoke(grid, "OnCellDoubleClick", new DataGridViewCellEventArgs(1, grid.CurrentRow!.Index)), edit => Assert.Equal(zulu.Id, edit.Value.Id));
            ObserveModal<global::WebSiteMonitor.SiteEditForm>(() => Invoke(main, "OnKeyDown", main, new KeyEventArgs(Keys.Control | Keys.E)), edit => Assert.Equal(zulu.Id, edit.Value.Id));
            ObserveModal<global::WebSiteMonitor.HistoryForm>(() => Item(menu, "更新履歴").PerformClick(), history => Assert.Equal(zulu.Id, history.SelectedSiteId));
            var toolbar = main.Controls.OfType<ToolStrip>().Single(strip => strip is not StatusStrip);
            ObserveModal<global::WebSiteMonitor.HistoryForm>(() => Item(toolbar, "更新履歴").PerformClick(), history => Assert.Null(history.SelectedSiteId));
            // Right-click a different displayed row, then keyboard-delete: confirmation and delete must agree.
            Invoke(main, "GridMouseDown", grid, new DataGridViewCellMouseEventArgs(1, 1, 0, 0, new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0)));
            main.DeleteConfirmation = site => { Assert.Equal(alpha.Id, site.Id); Assert.Equal("Alpha", site.Name); return true; };
            Invoke(main, "OnKeyDown", main, new KeyEventArgs(Keys.Delete)); Assert.Null(db.GetSite(alpha.Id)); Assert.NotNull(db.GetSite(zulu.Id));
            // A stale selected ID cannot resurrect or operate another row.
            db.DeleteSite(zulu.Id); Assert.Null(Invoke(main, "Selected")); Item(menu, "有効 / 無効").PerformClick(); Assert.Empty(db.GetSites());
            main.AllowExit(); main.Close();
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void HistoryFiltersSqlBeforeLimitHandlesDuplicatesDisabledEmptyAndNeverWrites(int font)
    {
        RunSta(() =>
        {
            var db = Db(); var quiet = Site("同名", "https://quiet.test/"); quiet.Enabled = false;
            var noisy = Site("同名", "https://noisy.test/"); var empty = Site("空履歴", "https://empty.test/");
            db.SaveSite(quiet); db.SaveSite(noisy); db.SaveSite(empty);
            var start = DateTimeOffset.Now.AddDays(-2);
            db.ApplySuccess(quiet.Id, "old", "[DIAGNOSTIC_REDACTED]", null, null, MonitorMode.Text, start);
            db.ApplySuccess(quiet.Id, "new", "Timeout website original", null, null, MonitorMode.Text, start.AddMinutes(1));
            db.ApplySuccess(noisy.Id, "0", "0", null, null, MonitorMode.Text, start.AddDays(1));
            db.ApplySuccess(noisy.Id, "1", "1", null, null, MonitorMode.Text, start.AddDays(1).AddSeconds(1));
            // A single test-owned transaction generates 1001 newer records.
            using (var c = new SqliteConnection($"Data Source={Path.Combine(_root, "test.db")}"))
            {
                c.Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO History(SiteId,ChangedAt,OldHash,NewHash,OldPreview,NewPreview,NotificationTargetUrl) VALUES($site,$at,'a',$hash,'a','b','https://noisy.test/')";
                cmd.Parameters.AddWithValue("$site", noisy.Id); var at = cmd.Parameters.AddWithValue("$at", ""); var hash = cmd.Parameters.AddWithValue("$hash", "");
                for (var n = 0; n < 1001; n++) { at.Value = start.AddDays(1).AddMinutes(n).ToString("O"); hash.Value = n.ToString(); cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            var before = db.GetHistory(quiet.Id).Single(); var pending = db.GetNextPendingUpdateDialog(); var baseline = db.GetSite(quiet.Id)!.LastHash;
            Assert.NotNull(pending);
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = font });
            using var notification = new global::WebSiteMonitor.UpdateDialog(pending!, () => throw new Exception("History must not acknowledge"), _ => { }); notification.Show();
            string? opened = null; using var history = new global::WebSiteMonitor.HistoryForm(db, null, url => opened = url);
            history.Show(); PumpUntil(() => history.CurrentLoad.IsCompleted);
            var grid = Get<DataGridView>(history, "_grid"); var filter = Get<ComboBox>(history, "_filter");
            Assert.Equal(1000, grid.Rows.Count); Assert.DoesNotContain(grid.Rows.Cast<DataGridViewRow>(), row => ((HistoryEntry)row.Tag!).SiteId == quiet.Id);
            Assert.Equal(4, filter.Items.Count); Assert.Equal(ComboBoxStyle.DropDownList, filter.DropDownStyle);
            var captions = filter.Items.Cast<object>().Select(item => item.ToString()).ToArray(); Assert.Contains(captions, s => s!.Contains("quiet.test")); Assert.Equal(4, captions.Distinct().Count());
            filter.SelectedIndex = Array.FindIndex(captions, s => s!.Contains("quiet.test")); PumpUntil(() => history.CurrentLoad.IsCompleted);
            Assert.Equal(quiet.Id, history.SelectedSiteId); Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("安全上の理由により内容を非表示", grid.Rows[0].Cells["Old"].Value); Assert.Equal("Timeout website original", grid.Rows[0].Cells["New"].Value);
            grid.Sort(grid.Columns["At"]!, ListSortDirection.Ascending);
            Descendants(history).OfType<Button>().Single(button => button.Text == "Webサイトを開く").PerformClick(); Assert.Equal(quiet.Url, opened);
            filter.SelectedIndex = Array.FindIndex(captions, s => s!.Contains("empty.test")); PumpUntil(() => history.CurrentLoad.IsCompleted);
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>()); Assert.Equal("このサイトの更新履歴はありません。", Get<Label>(history, "_message").Text);
            // Latest request wins even if a previous asynchronous read completes later.
            filter.SelectedIndex = 0; filter.SelectedIndex = Array.FindIndex(captions, s => s!.Contains("quiet.test")); PumpUntil(() => history.CurrentLoad.IsCompleted);
            Assert.Single(grid.Rows.Cast<DataGridViewRow>()); Assert.Equal(before, db.GetHistory(quiet.Id).Single()); Assert.Equal(baseline, db.GetSite(quiet.Id)!.LastHash); Assert.Equal(pending, db.GetNextPendingUpdateDialog());
            history.Close();
            notification.Shutdown(); Assert.Equal(pending, db.GetNextPendingUpdateDialog());
            using var invalid = new global::WebSiteMonitor.HistoryForm(db, long.MaxValue, _ => { }); Assert.Null(invalid.SelectedSiteId);
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void ToolbarAdjacencyVersionKeyboardAndWheelBehaviorPersistWithoutDoubleExecution(int initial)
    {
        RunSta(() =>
        {
            var db = Db(); var engine = new MonitorEngine(db, new MutableFetcher(), Logger()); using var scheduler = new global::WebSiteMonitor.OneShotScheduler(db, engine); using var sound = new global::WebSiteMonitor.SoundService();
            var settings = new AppSettings { UiFontSize = initial }; var store = new SettingsStore(Path.Combine(_root, "settings.json")); var saves = 0; var closes = 0; var restarts = 0;
            void Save(AppSettings value) { saves++; store.Save(value); }
            global::WebSiteMonitor.UiFontManager.Initialize(settings);
            using var main = new global::WebSiteMonitor.MainForm(db, engine, scheduler, sound, () => settings, Save, () => { }, _ => { }, restart: () => { restarts++; return Task.CompletedTask; }, exit: () => { closes++; return Task.CompletedTask; });
            using var editor = new global::WebSiteMonitor.SiteEditForm(null, engine, sound, settings, _root);
            using var history = new global::WebSiteMonitor.HistoryForm(db, null, _ => { }); using var preferences = new global::WebSiteMonitor.SettingsForm(settings);
            main.Show(); editor.Show(); history.Show(); preferences.Show(); Application.DoEvents();
            Assert.True(global::WebSiteMonitor.TrayApplicationContext.IsShutdownBlocked(false)); Assert.True(global::WebSiteMonitor.TrayApplicationContext.IsShutdownBlocked(true));
            Assert.Equal("WebSite Monitor v1.1.6", main.Text);
            var strip = main.Controls.OfType<ToolStrip>().Single(s => s is not StatusStrip); var restart = Item(strip, "再起動"); var close = Item(strip, "完全に閉じる");
            Assert.True(restart.Bounds.Right <= close.Bounds.Left); Assert.InRange(close.Bounds.Left - restart.Bounds.Right, 0, 8);
            foreach (var button in new[] { close, restart }) { Assert.Equal(ToolStripItemOverflow.Never, button.Overflow); Assert.True(button.Available); Assert.True(strip.ClientRectangle.Contains(button.Bounds)); }
            restart.PerformClick(); close.PerformClick(); Assert.Equal(1, restarts); Assert.Equal(1, closes);
            using var filter = new global::WebSiteMonitor.FontZoomMessageFilter(() => settings, Save);
            var target = Descendants(editor).OfType<TextBox>().First();
            var before = saves; var down = Message.Create(target.Handle, 0x0100, (IntPtr)(initial == 10 ? Keys.Oemplus : Keys.OemMinus), IntPtr.Zero);
            Assert.True(filter.HandleMessage(ref down, Keys.Control | Keys.Shift)); Assert.Equal(before + 1, saves); Assert.Equal(initial == 10 ? 11 : 17, settings.UiFontSize);
            var up = Message.Create(target.Handle, 0x0101, down.WParam, IntPtr.Zero); Assert.False(filter.HandleMessage(ref up, Keys.Control)); Assert.Equal(before + 1, saves);
            var wheel = Message.Create(target.Handle, 0x020A, (IntPtr)(120 << 16), IntPtr.Zero); Assert.False(filter.HandleMessage(ref wheel, Keys.Control)); Assert.False(filter.HandleMessage(ref wheel, Keys.None)); Assert.Equal(before + 1, saves);
            foreach (var form in new Form[] { main, editor, history, preferences }) Assert.InRange(form.Font.SizeInPoints, (float)settings.UiFontSize - 0.1f, (float)settings.UiFontSize + 0.1f);
            Assert.Equal(settings.UiFontSize, store.Load().UiFontSize);
            editor.Close(); history.Close(); preferences.Close();
            Assert.False(global::WebSiteMonitor.TrayApplicationContext.IsShutdownBlocked(false));
            foreach (var key in new[] { Keys.Add, Keys.Oemplus }) Assert.Equal(1, global::WebSiteMonitor.FontZoomMessageFilter.ZoomStep(key, Keys.Control));
            foreach (var key in new[] { Keys.Subtract, Keys.OemMinus }) Assert.Equal(-1, global::WebSiteMonitor.FontZoomMessageFilter.ZoomStep(key, Keys.Control));
            foreach (var key in new[] { Keys.N, Keys.E, Keys.Enter, Keys.ProcessKey }) Assert.Equal(0, global::WebSiteMonitor.FontZoomMessageFilter.ZoomStep(key, Keys.Control));
            Assert.Equal(0, global::WebSiteMonitor.FontZoomMessageFilter.ZoomStep(Keys.Oemplus, Keys.None)); Assert.Equal(0, global::WebSiteMonitor.FontZoomMessageFilter.ZoomStep(Keys.Add, Keys.Control | Keys.Alt));
            for (var n = 0; n < 30; n++) { var message = Message.Create(main.Handle, 0x0100, (IntPtr)Keys.Add, IntPtr.Zero); Assert.True(filter.HandleMessage(ref message, Keys.Control)); }
            Assert.Equal(18, settings.UiFontSize);
            for (var n = 0; n < 30; n++) { var message = Message.Create(main.Handle, 0x0100, (IntPtr)Keys.Subtract, IntPtr.Zero); Assert.True(filter.HandleMessage(ref message, Keys.Control)); }
            Assert.Equal(10, settings.UiFontSize); Assert.Equal(10, store.Load().UiFontSize);
            editor.Close(); history.Close(); preferences.Close(); main.AllowExit(); main.Close();
        });
    }

    [Fact]
    public async Task ChangedLogIsCommittedOnceEvenWhenNotificationsDisabledAndStorageFails()
    {
        var db = Db(); var site = Site("変更ログ", "https://monitor.test/page?view=1"); site.NotificationTargetUrl = "https://destination.test/"; db.SaveSite(site);
        var fetcher = new MutableFetcher(); var engine = new MonitorEngine(db, fetcher, Logger(), () => false);
        Assert.Equal(CheckOutcome.BaselineCreated, (await engine.CheckAsync(site, default)).Outcome);
        Assert.Equal(CheckOutcome.Unchanged, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Status = 304; Assert.Equal(CheckOutcome.Unchanged, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Status = 200; fetcher.Body = "changed"; Assert.Equal(CheckOutcome.Changed, (await engine.CheckAsync(site, default)).Outcome);
        Assert.Equal(CheckOutcome.Unchanged, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Error = new HttpRequestException("unsafe", null, System.Net.HttpStatusCode.Forbidden); Assert.Equal(CheckOutcome.Failed, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Error = new TimeoutException("unsafe"); Assert.Equal(CheckOutcome.Failed, (await engine.CheckAsync(site, default)).Outcome);
        fetcher.Error = new OperationCanceledException(); using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CheckAsync(site, canceled.Token));
        var changed = Logs().Split('\n').Where(line => line.Contains(" | 更新検知 | ")).ToArray(); Assert.Single(changed);
        Assert.Matches(@"^\d{4}年\d{2}月\d{2}日 \d{2}時\d{2}分 \| 更新検知 \| サイト名：変更ログ \| URL：https://monitor.test/$", changed[0]);
        Assert.DoesNotContain("destination.test", changed[0]); Assert.Single(db.GetHistory());
        Directory.CreateDirectory(_root); var blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "not a directory");
        fetcher.Error = null; fetcher.Body = "again"; var brokenLogEngine = new MonitorEngine(db, fetcher, new FileLogger(blocked));
        Assert.Equal(CheckOutcome.Changed, (await brokenLogEngine.CheckAsync(site, default)).Outcome); Assert.Equal(2, db.GetHistory().Count);
    }

    [Fact]
    public async Task StaleMonitoringGenerationNeverEmitsChangedLog()
    {
        var db = Db(); var site = Site("世代", "https://monitor.test/"); db.SaveSite(site); var fetcher = new MutableFetcher(); var engine = new MonitorEngine(db, fetcher, Logger());
        await engine.CheckAsync(site, default); fetcher.Body = "changed"; fetcher.BeforeReturn = () => { var newer = db.GetSite(site.Id)!; newer.Url = "https://new.test/"; db.SaveSite(newer); };
        Assert.Equal(CheckOutcome.Discarded, (await engine.CheckAsync(site, default)).Outcome); Assert.DoesNotContain(" | 更新検知 | ", Logs()); Assert.Empty(db.GetHistory());
    }

    [Theory]
    [InlineData("https://user:private@example.test/token/opaque?token=private#secret", "https://example.test/")]
    [InlineData("https://example.test/sensitive-opaque-path?view=private#private", "https://example.test/")]
    [InlineData("https://example.test/%74%6f%6b%65%6e/private", "https://example.test/")]
    [InlineData("not-a-url\nprivate", "機密情報保護のためURLを省略")]
    [InlineData("https://example.test/\r\nprivate", "機密情報保護のためURLを省略")]
    public void DedicatedLogKeepsOnlyPublicOriginAndRejectsNameInjection(string url, string expected)
    {
        var logger = Logger(); logger.UpdateDetected("名前\r\n\t | token=private Bearer hidden", url, DateTimeOffset.Now);
        var lines = Logs().Split('\n', StringSplitOptions.RemoveEmptyEntries); Assert.Single(lines); Assert.EndsWith("URL：" + expected, lines[0]); Assert.DoesNotContain("private", lines[0]); Assert.DoesNotContain("hidden", lines[0]);
        logger.Info("URL=https://example.test/private?token=private"); logger.Error("Bearer hidden", new Exception("password=private"));
        var all = Logs(); Assert.DoesNotContain("private", all); Assert.DoesNotContain("hidden", all);
        Assert.Equal(expected.StartsWith("https://", StringComparison.Ordinal) ? 1 : 0, all.Split('\n').Count(line => line.Contains("https://example.test/")));
    }

    [Fact]
    public void RenamedExecutableShortcutUsesProvidedPathAndWorkingDirectory()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(_root); var exe = Path.Combine(_root, "日本語 空白", "WebSiteMonitor_v1.1.2.exe"); Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "test-owned placeholder");
            var linkPath = Path.Combine(_root, "probe.lnk"); global::WebSiteMonitor.ShortcutInstaller.Create(exe, linkPath, global::WebSiteMonitor.WindowsIntegration.AppId);
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object? link = null;
            try { link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [linkPath]); Assert.Equal(exe, (string)link!.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null)!); Assert.Equal(Path.GetDirectoryName(exe), (string)link.GetType().InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null)!); }
            finally { if (link is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    public void HundredSitesAndLongNamesKeepHistoryFilterUsable(int font)
    {
        RunSta(() =>
        {
            var db = Db();
            for (var n = 0; n < 100; n++) db.SaveSite(Site(n == 0 ? new string('長', 100) : $"サイト{n:000}", $"https://site{n}.test/"));
            global::WebSiteMonitor.UiFontManager.Initialize(new AppSettings { UiFontSize = font });
            using var form = new global::WebSiteMonitor.HistoryForm(db, null, _ => { }); form.Show(); PumpUntil(() => form.CurrentLoad.IsCompleted);
            var combo = Get<ComboBox>(form, "_filter"); Assert.Equal(101, combo.Items.Count);
            var index = combo.Items.Cast<object>().Select((item, position) => (item, position)).Single(pair => pair.item.ToString()!.Contains("site0.test")).position;
            combo.SelectedIndex = index; PumpUntil(() => form.CurrentLoad.IsCompleted);
            Assert.True(combo.Width > 300); Assert.True(form.ClientRectangle.Contains(form.PointToClient(combo.PointToScreen(System.Drawing.Point.Empty))));
            Assert.InRange(combo.DropDownWidth, combo.Width, Math.Max(combo.Width, Screen.FromControl(form).WorkingArea.Width));
            Assert.Contains(new string('長', 100), Get<ToolTip>(form, "_filterTip").GetToolTip(combo));
            var renamed = db.GetSite(form.SelectedSiteId!.Value)!; renamed.Name = "改名後"; db.SaveSite(renamed);
            combo.SelectedIndex = 0; combo.SelectedIndex = index; PumpUntil(() => form.CurrentLoad.IsCompleted); Assert.Equal(renamed.Id, form.SelectedSiteId);
            db.DeleteSite(renamed.Id); combo.SelectedIndex = 0; combo.SelectedIndex = index; PumpUntil(() => form.CurrentLoad.IsCompleted); Assert.Empty(Get<DataGridView>(form, "_grid").Rows.Cast<DataGridViewRow>());
            form.Close();
        });
    }

    [Fact]
    public void ValidationAndAudioErrorsDoNotShowExternalEnglishOrSecretBodies()
    {
        var css = Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateCssSelector("["));
        Assert.Equal("CSSセレクターの構文が正しくありません。", global::WebSiteMonitor.DisplayText.InputError(css));
        var xpath = Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateXPath("["));
        Assert.Equal("XPathの構文が正しくありません。", global::WebSiteMonitor.DisplayText.InputError(xpath));
        var regex = Assert.Throws<ArgumentException>(() => ContentExtractor.ValidateRegex("["));
        Assert.Equal("正規表現の構文が正しくありません。", global::WebSiteMonitor.DisplayText.InputError(regex));
        var audio = new global::WebSiteMonitor.SoundPlaybackResult(global::WebSiteMonitor.SoundPlaybackState.Failed, "external value", new IOException("unknown-secret token=secret"));
        var safe = global::WebSiteMonitor.DisplayText.SoundError(audio); Assert.DoesNotContain("unknown-secret", safe); Assert.DoesNotContain("external value", safe); Assert.Contains("PATH", safe); Assert.Contains("DRM", safe); Assert.DoesNotContain("REDACTED", safe);
    }

    private sealed class MutableFetcher : IHttpFetcher
    {
        public string Body = "original"; public int Status = 200; public Exception? Error; public Action? BeforeReturn;
        public List<Uri> Requests { get; } = [];
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? modified, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add(uri); if (Error is not null) throw Error; BeforeReturn?.Invoke(); return Task.FromResult(new HttpFetchResult(Status, Encoding.UTF8.GetBytes(Body), "text/html", "utf-8", null, null, uri)); }
    }
    private static ToolStripItem Item(ToolStrip strip, string text) => strip.Items.Cast<ToolStripItem>().Single(item => item.Text == text);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static IEnumerable<Control> Descendants(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void ObserveModal<T>(Action open, Action<T> check) where T : Form
    {
        Exception? error = null; var seen = false; using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) => { var form = Application.OpenForms.OfType<T>().FirstOrDefault(); if (form is null) return; timer.Stop(); try { check(form); seen = true; } catch (Exception ex) { error = ex; } finally { form.DialogResult = DialogResult.Cancel; form.Close(); } };
        timer.Start(); open(); timer.Stop(); if (error is not null) ExceptionDispatchInfo.Capture(error).Throw(); Assert.True(seen);
    }
    private static void PumpUntil(Func<bool> ready) { var watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(12)) { Application.DoEvents(); Thread.Sleep(5); } Assert.True(ready()); Application.DoEvents(); }
    private static void RunSta(Action action) { Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(120000)); if (error is not null) ExceptionDispatchInfo.Capture(error).Throw(); }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
