using System.Drawing;
using System.Windows.Forms;
using System.Runtime.ExceptionServices;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class UiLayoutTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "WebSiteMonitorUiTests-" + Guid.NewGuid().ToString("N"));

    public UiLayoutTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact]
    public void MainFormShowsFullJapaneseCommandsAndGridHeaders()
    {
        RunSta(() =>
        {
            var database = NewDatabase();
            using var fetcher = new SharedHttpFetcher(new HttpClient(new EmptyHandler()));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs"))));
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs2"))), scheduler, sound, () => new AppSettings(), _ => { }, () => { }, _ => { });
            form.CreateControl();
            ShowForLayout(form);
            Assert.True(form.MinimumSize.Width >= 980);
            var toolbar = FindAll<ToolStrip>(form).Single(strip => strip is not StatusStrip);
            Assert.Contains(toolbar.Items.Cast<ToolStripItem>(), item => item.Text == "全て確認");
            Assert.Contains(toolbar.Items.Cast<ToolStripItem>(), item => item.Text == "一時停止 / 再開");
            var grid = FindAll<DataGridView>(form).Single();
            Assert.Equal(["有効", "サイト名", "URL", "監視方式", "スケジュール", "次回確認", "最終確認", "最終更新", "状態"], grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).ToArray());
            Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column => Assert.True(column.Width >= column.MinimumWidth));
            AssertNoTextClipping(form);
        });
    }

    [Fact]
    public void SiteEditorHasThreeVisiblePasteButtonsAndReadableControls()
    {
        RunSta(() =>
        {
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.SiteEditForm(null, new MonitorEngine(NewDatabase(), new EmptyFetcher(), new FileLogger(Path.Combine(_temp, "logs"))), sound, new AppSettings(), Path.Combine(_temp, "sounds"));
            form.CreateControl();
            ShowForLayout(form);
            Assert.True(form.MinimumSize.Width >= 860);
            Assert.Equal(3, FindAll<Button>(form).Count(button => button.Text == "貼り付け"));
            Assert.Contains(FindAll<Button>(form), button => button.Text == "監視テスト / 抽出プレビュー" && button.Visible);
            Assert.Contains(FindAll<CheckBox>(form), box => box.Text == "独自ポップアップ" && box.Visible);
            AssertNoVisibleControlHasEmptyBounds(form);
            AssertNoTextClipping(form);
        });
    }

    [Fact]
    public void AuxiliaryFormsExposeFullSettingsHistoryAndEmbeddedLicenses()
    {
        RunSta(() =>
        {
            var database = NewDatabase();
            var site = new Site { Name = "履歴確認", Url = "https://example.test/", MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" };
            database.SaveSite(site);
            database.ApplySuccess(site.Id, "A", "old", null, null, MonitorMode.Text, DateTimeOffset.Now);
            database.ApplySuccess(site.Id, "B", "new", null, null, MonitorMode.Text, DateTimeOffset.Now);
            using var settings = new global::WebSiteMonitor.SettingsForm(new AppSettings());
            using var history = new global::WebSiteMonitor.HistoryForm(database, site.Id, _ => { });
            using var about = new global::WebSiteMonitor.AboutForm();
            using var popup = new global::WebSiteMonitor.UpdatePopup(site, _ => { });
            settings.CreateControl(); history.CreateControl(); about.CreateControl(); popup.CreateControl();
            ShowForLayout(settings, history, about, popup);
            Assert.Contains(FindAll<CheckBox>(settings), box => box.Text == "Windows通知・スタートメニュー統合を有効にする");
            Assert.Contains(FindAll<Button>(settings), button => button.Text == "Windows統合情報を解除");
            Assert.Contains(FindAll<Button>(settings), button => button.Text == "About / 第三者ライセンス");
            var historyGrid = FindAll<DataGridView>(history).Single();
            Assert.Contains(historyGrid.Columns.Cast<DataGridViewColumn>(), column => column.HeaderText == "変更前プレビュー");
            Assert.Contains(historyGrid.Columns.Cast<DataGridViewColumn>(), column => column.HeaderText == "変更後プレビュー");
            Assert.Contains(FindAll<TextBox>(about), box => box.Text.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(FindAll<Button>(popup), button => button.Text == "Webサイトを開く");
            AssertNoVisibleControlHasEmptyBounds(settings);
            AssertNoVisibleControlHasEmptyBounds(history);
            AssertNoVisibleControlHasEmptyBounds(about);
            AssertNoVisibleControlHasEmptyBounds(popup);
            AssertNoTextClipping(settings);
            AssertNoTextClipping(history);
            AssertNoTextClipping(about);
            AssertNoTextClipping(popup);
        });
    }

    private Database NewDatabase()
    {
        var database = new Database(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }

    private static void ShowForLayout(params Form[] forms)
    {
        foreach (var form in forms) form.Show();
        Application.DoEvents();
        foreach (var form in forms) form.PerformLayout();
    }

    private static IEnumerable<T> FindAll<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static void AssertNoVisibleControlHasEmptyBounds(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Visible) Assert.True(child.Width > 0 && child.Height > 0, $"{child.GetType().Name} ({child.Text}) has empty bounds.");
            AssertNoVisibleControlHasEmptyBounds(child);
        }
    }

    [Fact]
    public void MainFormCloseDestroysItsHandleAndAReplacementCanBeCreated()
    {
        RunSta(() =>
        {
            var database = NewDatabase();
            using var fetcher = new SharedHttpFetcher(new HttpClient(new EmptyHandler()));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs"))));
            using var sound = new global::WebSiteMonitor.SoundService();
            var saved = new AppSettings();
            var first = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs-a"))), scheduler, sound, () => saved, value => saved = value, () => { }, _ => { });
            ShowForLayout(first);
            Assert.True(first.IsHandleCreated);
            first.Close();
            Application.DoEvents();
            Assert.False(first.IsHandleCreated);
            first.Dispose();
            Assert.True(first.IsDisposed);
            using var replacement = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "logs-b"))), scheduler, sound, () => saved, _ => { }, () => { }, _ => { });
            ShowForLayout(replacement);
            Assert.True(replacement.IsHandleCreated);
            AssertNoTextClipping(replacement);
        });
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(14.0)]
    [InlineData(18.0)]
    public void MajorFormsRemainMeasuredAndScrollableAtSupportedFontSizes(double fontSize)
    {
        RunSta(() =>
        {
            var settingsValue = new AppSettings { UiFontSize = fontSize };
            var database = NewDatabase();
            var site = new Site { Name = "日本語レイアウト確認サイト", Url = "https://example.test/", MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" };
            database.SaveSite(site);
            using var fetcher = new SharedHttpFetcher(new HttpClient(new EmptyHandler()));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "font-logs"))));
            using var sound = new global::WebSiteMonitor.SoundService();
            using var main = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "font-logs-main"))), scheduler, sound, () => settingsValue, value => settingsValue = value, () => { }, _ => { });
            using var settings = new global::WebSiteMonitor.SettingsForm(settingsValue);
            using var editor = new global::WebSiteMonitor.SiteEditForm(site, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "font-logs-editor"))), sound, settingsValue, Path.Combine(_temp, "sounds"));
            using var history = new global::WebSiteMonitor.HistoryForm(database, site.Id, _ => { });
            using var about = new global::WebSiteMonitor.AboutForm();
            using var popup = new global::WebSiteMonitor.UpdatePopup(site, _ => { });
            ShowForLayout(main, settings, editor, history, about, popup);

            foreach (var form in new Form[] { main, settings, editor, history, about, popup })
            {
                Assert.InRange(form.Font.SizeInPoints, (float)fontSize - 0.1f, (float)fontSize + 0.1f);
                AssertNoVisibleControlHasEmptyBounds(form);
                AssertNoTextClipping(form);
                AssertDpiEquivalentMeasurements(form);
            }
        });
    }

    [Fact]
    public void FontZoomClampsPersistsAndKeepsScrollableControlsOnTheirNormalWheelPath()
    {
        RunSta(() =>
        {
            var settings = new AppSettings();
            global::WebSiteMonitor.UiFontManager.Initialize(settings);
            var saves = 0;
            Assert.False(global::WebSiteMonitor.UiFontManager.Change(settings, -1, _ => saves++));
            Assert.Equal(10.0, settings.UiFontSize);
            Assert.True(global::WebSiteMonitor.UiFontManager.Change(settings, 1, _ => saves++));
            Assert.Equal(11.0, settings.UiFontSize);
            for (var index = 0; index < 20; index++) global::WebSiteMonitor.UiFontManager.Change(settings, 1, _ => saves++);
            Assert.Equal(18.0, settings.UiFontSize);
            Assert.True(saves > 0);
            Assert.True(global::WebSiteMonitor.UiFontManager.ShouldZoom(new Label(), false));
            Assert.False(global::WebSiteMonitor.UiFontManager.ShouldZoom(new DataGridView(), false));
            Assert.False(global::WebSiteMonitor.UiFontManager.ShouldZoom(new TextBox { Multiline = true }, false));
            Assert.False(global::WebSiteMonitor.UiFontManager.ShouldZoom(new Panel { AutoScroll = true }, false));
            Assert.True(global::WebSiteMonitor.UiFontManager.ShouldZoom(new DataGridView(), true));
        });
    }
    [Theory]
    [InlineData(10.0)]
    [InlineData(14.0)]
    [InlineData(18.0)]
    public void MainFormReflowsWithoutTextClippingAtMinimumStandardAndExpandedSizes(double fontSize)
    {
        RunSta(() =>
        {
            var settings = new AppSettings { UiFontSize = fontSize };
            var database = NewDatabase();
            using var fetcher = new SharedHttpFetcher(new HttpClient(new EmptyHandler()));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "resize-logs"))));
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "resize-logs-main"))), scheduler, sound, () => settings, _ => { }, () => { }, _ => { });
            ShowForLayout(form);
            foreach (var size in new[] { form.MinimumSize, new Size(1180, 720), new Size(1600, 1000) })
            {
                form.Size = new Size(Math.Max(size.Width, form.MinimumSize.Width), Math.Max(size.Height, form.MinimumSize.Height));
                Application.DoEvents();
                form.PerformLayout();
                AssertNoVisibleControlHasEmptyBounds(form);
                AssertNoTextClipping(form);
                AssertDpiEquivalentMeasurements(form);
            }
        });
    }
    [Theory]
    [InlineData(10.0)]
    [InlineData(14.0)]
    [InlineData(18.0)]
    public void StateFillTracksWideAndNarrowMainFormWithoutChangingFixedWidths(double fontSize)
    {
        RunSta(() =>
        {
            var settings = new AppSettings { UiFontSize = fontSize, ColumnWidths = new Dictionary<string, int> { ["Name"] = 205, ["Url"] = 305 } };
            var database = NewDatabase();
            database.SaveSite(new Site { Name = "幅検査", Url = "https://example.test/", MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" });
            using var fetcher = new SharedHttpFetcher(new HttpClient(new EmptyHandler()));
            using var scheduler = new global::WebSiteMonitor.OneShotScheduler(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "fill-scheduler"))));
            using var sound = new global::WebSiteMonitor.SoundService();
            using var form = new global::WebSiteMonitor.MainForm(database, new MonitorEngine(database, fetcher, new FileLogger(Path.Combine(_temp, "fill-engine"))), scheduler, sound, () => settings, value => settings = value, () => { }, _ => { });
            ShowForLayout(form);
            var grid = FindAll<DataGridView>(form).Single();
            var state = grid.Columns["State"]!;
            Assert.Equal(DataGridViewAutoSizeColumnMode.Fill, state.AutoSizeMode);
            Assert.Equal(205, grid.Columns["Name"]!.Width);
            Assert.Equal(305, grid.Columns["Url"]!.Width);
            foreach (var width in new[] { 980, 1180, 1600, 1920 })
            {
                form.Width = width;
                Application.DoEvents();
                form.PerformLayout();
                Assert.True(state.Width >= state.MinimumWidth);
                Assert.Equal(205, grid.Columns["Name"]!.Width);
                Assert.Equal(305, grid.Columns["Url"]!.Width);
                if (width >= 1600)
                {
                    var right = grid.GetCellDisplayRectangle(state.Index, 0, false).Right;
                    Assert.InRange(grid.ClientSize.Width - right, 0, 5);
                }
                else Assert.True(grid.Columns.Cast<DataGridViewColumn>().Sum(column => column.MinimumWidth) > 0);
                AssertGridHeadersFit(grid);
            }
            form.AllowExit();
            form.Close();
            Assert.False(settings.ColumnWidths.ContainsKey("State"));
            Assert.Equal(205, settings.ColumnWidths["Name"]);
            Assert.Equal(305, settings.ColumnWidths["Url"]);
        });
    }

    private static void AssertNoTextClipping(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Visible)
            {
                if (child is DataGridView grid) AssertGridHeadersFit(grid);
                if (child is ComboBox combo) AssertComboItemsFit(combo);
                if (IsFixedTextControl(child)) AssertControlTextFits(child);
                AssertNoTextClipping(child);
            }
        }
        foreach (var strip in FindAll<ToolStrip>(parent)) AssertToolStripItemsFit(strip);
    }

    private static bool IsFixedTextControl(Control control)
        => control is Label or Button or CheckBox or RadioButton or GroupBox
           && !control.AutoSize && !string.IsNullOrWhiteSpace(control.Text);

    private static void AssertControlTextFits(Control control)
    {
        var required = MeasureTextWidth(control.Text, control.Font) + control.Padding.Horizontal + 6;
        if (control is CheckBox or RadioButton) required += 24;
        Assert.True(control.ClientSize.Width >= required, $"{control.GetType().Name} text is clipped: {control.Text} (need {required}, have {control.ClientSize.Width}).");
    }

    private static void AssertComboItemsFit(ComboBox combo)
    {
        if (combo.DropDownStyle == ComboBoxStyle.Simple) return;
        foreach (var item in combo.Items.Cast<object>())
        {
            var text = item.ToString() ?? string.Empty;
            Assert.False(item.GetType().IsEnum, $"Enum value leaked into ComboBox: {text}");
            Assert.True(combo.ClientSize.Width >= MeasureTextWidth(text, combo.Font) + 12, $"ComboBox item is clipped: {text}");
        }
    }

    private static void AssertGridHeadersFit(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            var required = MeasureTextWidth(column.HeaderText, grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font) + 12;
            Assert.True(column.Width >= required, $"Grid header is clipped: {column.HeaderText} (need {required}, have {column.Width}).");
        }
    }

    private static void AssertToolStripItemsFit(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items)
        {
            if (item.AutoSize || string.IsNullOrWhiteSpace(item.Text)) continue;
            var required = MeasureTextWidth(item.Text, item.Font) + item.Padding.Horizontal + item.Margin.Horizontal + 8;
            Assert.True(item.Width >= required, $"ToolStrip item is clipped: {item.Text} (need {required}, have {item.Width}).");
        }
    }

    private static int MeasureTextWidth(string text, Font font)
        => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    private static void AssertDpiEquivalentMeasurements(Control parent)
    {
        foreach (var dpiScale in new[] { 1.00F, 1.25F, 1.50F, 1.75F, 2.00F })
        {
            foreach (var control in FindAll<Control>(parent).Prepend(parent).Where(control => control.Visible && control is Label or Button or CheckBox or RadioButton or GroupBox))
            {
                if (!IsFixedTextControl(control)) continue;
                using var scaled = new Font(control.Font.FontFamily, control.Font.SizeInPoints * dpiScale, control.Font.Style);
                var required = MeasureTextWidth(control.Text, scaled) + control.Padding.Horizontal + 6;
                if (control is CheckBox or RadioButton) required += 24;
                Assert.True(control.ClientSize.Width * dpiScale >= required, $"DPI {dpiScale:0.00} text is clipped: {control.Text}");
            }
            foreach (var grid in FindAll<DataGridView>(parent))
            {
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    using var scaled = new Font(grid.Font.FontFamily, grid.Font.SizeInPoints * dpiScale, grid.Font.Style);
                    var required = MeasureTextWidth(column.HeaderText, scaled) + 16;
                    Assert.True(column.Width * dpiScale >= required, $"DPI {dpiScale:0.00} grid header is clipped: {column.HeaderText}");
                }
            }
        }
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        using var complete = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { complete.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(complete.Wait(TimeSpan.FromSeconds(20)), "STA UI test timed out.");
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class EmptyFetcher : IHttpFetcher
    {
        public Task<HttpFetchResult> FetchAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken) => throw new InvalidOperationException("Network is not used by UI layout tests.");
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
