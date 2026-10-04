using System.Diagnostics;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class MainForm : Form
{
    private readonly Database _database;
    private readonly MonitorEngine _engine;
    private readonly OneShotScheduler _scheduler;
    private readonly SoundService _sound;
    private readonly Func<AppSettings> _getSettings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly Action _showSettings;
    private readonly Action<string> _openUrl;
    private readonly DataGridView _grid = new();
    private readonly ToolStripStatusLabel _status = new();
    private bool _allowExit;
    private readonly Func<bool> _configurationBusy;
    private List<Site> _sites = [];

    public MainForm(Database database, MonitorEngine engine, OneShotScheduler scheduler, SoundService sound, Func<AppSettings> getSettings, Action<AppSettings> saveSettings, Action showSettings, Action<string> openUrl, Func<bool, Task>? transferSettings = null, Func<bool>? configurationBusy = null)
    {
        _database = database;
        _engine = engine;
        _scheduler = scheduler;
        _sound = sound;
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _showSettings = showSettings;
        _openUrl = openUrl;
        _configurationBusy = configurationBusy ?? (() => false);
        Text = "WebSite Monitor";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(980, 580);
        KeyPreview = true;
        var settings = getSettings();
        Size = new Size(Math.Max(settings.WindowWidth, MinimumSize.Width), Math.Max(settings.WindowHeight, MinimumSize.Height));
        if (settings.WindowX >= 0 && settings.WindowY >= 0) Location = new Point(settings.WindowX, settings.WindowY); else StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, AutoSize = true, Padding = new Padding(4, 3, 4, 3), ImageScalingSize = new Size(20, 20) };
        AddToolButton(toolbar, "追加", (_, _) => EditSite(null));
        AddToolButton(toolbar, "編集", (_, _) => EditSelected());
        AddToolButton(toolbar, "削除", (_, _) => DeleteSelected());
        toolbar.Items.Add(new ToolStripSeparator());
        AddToolButton(toolbar, "今すぐ確認", async (_, _) => await CheckSelectedAsync());
        AddToolButton(toolbar, "全て確認", async (_, _) => await _scheduler.CheckNowAsync(_database.GetSites().Where(s => s.Enabled)));
        AddToolButton(toolbar, "一時停止 / 再開", (_, _) => { _scheduler.TogglePause(); RefreshStatus(); });
        toolbar.Items.Add(new ToolStripSeparator());
        AddToolButton(toolbar, "更新履歴", (_, _) => ShowHistory());
        AddToolButton(toolbar, "設定", (_, _) => _showSettings());
        var transferMenu = new ToolStripDropDownButton("設定入出力") { AutoSize = true, DisplayStyle = ToolStripItemDisplayStyle.Text };
        transferMenu.DropDownItems.Add("設定をエクスポート", null, async (_, _) => { if (transferSettings is not null) await transferSettings(false); });
        transferMenu.DropDownItems.Add("設定をインポート", null, async (_, _) => { if (transferSettings is not null) await transferSettings(true); });
        toolbar.Items.Add(transferMenu);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.EnableHeadersVisualStyles = true;
        AddColumn("Enabled", "有効", 55);
        AddColumn("Name", "サイト名", 170);
        AddColumn("Url", "URL", 280);
        AddColumn("Mode", "監視方式", 115);
        AddColumn("Schedule", "スケジュール", 135);
        AddColumn("Next", "次回確認", 145);
        AddColumn("Checked", "最終確認", 145);
        AddColumn("Changed", "最終更新", 145);
        AddColumn("State", "状態", 180);
        foreach (DataGridViewColumn column in _grid.Columns)
            if (column.Name != "State" && settings.ColumnWidths.TryGetValue(column.Name, out var width)) column.Width = Math.Max(column.MinimumWidth, width);
        _grid.Columns["State"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.FontChanged += (_, _) => UpdateStateMinimumWidth();
        UpdateStateMinimumWidth();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        _grid.CellMouseDown += GridMouseDown;

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(_status);
        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
        toolbar.Dock = DockStyle.Top;
        statusStrip.Dock = DockStyle.Bottom;
        FormClosing += OnClosing;
        FormClosed += (_, _) => SaveLayout();
        KeyDown += OnKeyDown;
        Shown += (_, _) => Reload();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        BuildContextMenu();
        UiFontManager.Apply(this, settings.UiFontSize);
    }

    private static void AddToolButton(ToolStrip strip, string text, EventHandler handler)
    {
        strip.Items.Add(new ToolStripButton(text, null, handler) { AutoSize = true, DisplayStyle = ToolStripItemDisplayStyle.Text });
    }

    private void AddColumn(string name, string header, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            MinimumWidth = Math.Min(width, 90),
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        });
    }

    private void UpdateStateMinimumWidth()
    {
        var state = _grid.Columns["State"]!;
        var values = new[] { state.HeaderText, "未確認", "正常", "エラー（9999回）" };
        state.MinimumWidth = values.Max(value => TextRenderer.MeasureText(value, _grid.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width) + 20;
    }

    public void Reload()
    {
        if (IsDisposed) return;
        _sites = _database.GetSites();
        _grid.Rows.Clear();
        foreach (var site in _sites)
        {
            _grid.Rows.Add(site.Enabled ? "✓" : "", NotificationUrl.RedactText(site.Name), NotificationUrl.ForSite(site), site.EffectiveMode is null ? ModeText(site.MonitorMode) : ModeTextOrValue(site.EffectiveMode),
                ScheduleText(site), DateText(site.NextDue), DateText(site.LastChecked), DateText(site.LastChanged),
                site.LastError is null ? (site.LastChecked is null ? "未確認" : "正常") : $"エラー（{site.ConsecutiveErrors}回）");
        }
        RefreshStatus();
    }

    public void RefreshStatus() => _status.Text = $"{(_scheduler.Paused ? "一時停止中" : "監視中")}  |  有効 {_sites.Count(s => s.Enabled)}件  |  処理中 {_scheduler.RunningCount}件";
    private Site? Selected() => _grid.CurrentRow?.Index is int index && index >= 0 && index < _sites.Count ? _sites[index] : null;

    private void EditSite(Site? site)
    {
        using var form = new SiteEditForm(site, _engine, _sound, _getSettings(), Path.Combine(AppContext.BaseDirectory, "data", "sounds"));
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _database.SaveSite(form.Value);
            _scheduler.ScheduleChanged();
            Reload();
        }
    }

    private void EditSelected() { var site = Selected(); if (site is not null) EditSite(site); }

    private void DeleteSelected()
    {
        var site = Selected();
        if (site is null) return;
        if (MessageBox.Show($"「{site.Name}」を削除しますか？\n更新履歴も削除されます。", "削除確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _database.DeleteSite(site.Id);
        _scheduler.ScheduleChanged();
        Reload();
    }

    private async Task CheckSelectedAsync()
    {
        var site = Selected();
        if (site is not null) await _scheduler.CheckNowAsync([site]);
    }

    private void ShowHistory()
    {
        using var form = new HistoryForm(_database, Selected()?.Id, _openUrl);
        form.ShowDialog(this);
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Webサイトを開く", null, (_, _) => { var site = Selected(); if (site is not null) { var safe = NotificationUrl.ForSite(site); if (safe.Length != 0) _openUrl(safe); } });
        menu.Items.Add("今すぐ確認", null, async (_, _) => await CheckSelectedAsync());
        menu.Items.Add("編集", null, (_, _) => EditSelected());
        menu.Items.Add("有効 / 無効", null, (_, _) =>
        {
            var site = Selected();
            if (site is null) return;
            site.Enabled = !site.Enabled;
            _database.SaveSite(site);
            _scheduler.ScheduleChanged();
            Reload();
        });
        menu.Items.Add("更新履歴", null, (_, _) => ShowHistory());
        menu.Items.Add("削除", null, (_, _) => DeleteSelected());
        _grid.ContextMenuStrip = menu;
        UiFontManager.Register(menu, _getSettings().UiFontSize);
    }

    private void GridMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
        _grid.ClearSelection();
        _grid.Rows[e.RowIndex].Selected = true;
        _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.N) { EditSite(null); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.E) { EditSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { _ = CheckSelectedAsync(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_configurationBusy()) { e.Cancel = true; return; }
        if (_allowExit || e.CloseReason == CloseReason.UserClosing) SaveLayout();
    }

    public void AllowExit() => _allowExit = true;

    public void ApplyImportedSettings()
    {
        var settings = _getSettings();
        WindowState = FormWindowState.Normal;
        Size = new Size(Math.Max(settings.WindowWidth, MinimumSize.Width), Math.Max(settings.WindowHeight, MinimumSize.Height));
        if (settings.WindowX >= 0 && settings.WindowY >= 0) Location = new Point(settings.WindowX, settings.WindowY);
        foreach (DataGridViewColumn column in _grid.Columns)
            if (column.Name != "State" && settings.ColumnWidths.TryGetValue(column.Name, out var width)) column.Width = Math.Max(column.MinimumWidth, width);
        Reload();
    }

    private void SaveLayout()
    {
        if (_configurationBusy()) return;
        if (WindowState == FormWindowState.Minimized) return;
        var settings = _getSettings();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.WindowX = bounds.X;
        settings.WindowY = bounds.Y;
        settings.WindowWidth = Math.Max(bounds.Width, MinimumSize.Width);
        settings.WindowHeight = Math.Max(bounds.Height, MinimumSize.Height);
        settings.ColumnWidths = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != "State").ToDictionary(c => c.Name, c => c.Width);
        _saveSettings(settings);
    }

    private static string DateText(DateTimeOffset? date) => date?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "-";
    private static string ScheduleText(Site site) => site.ScheduleMode switch
    {
        ScheduleMode.Interval => $"{site.IntervalMinutes}分ごと",
        ScheduleMode.Daily => $"毎日 {site.DailyTime}",
        _ => "手動"
    };
    private static string ModeTextOrValue(string mode) => Enum.TryParse<MonitorMode>(mode, out var value) ? ModeText(value) : mode;
    internal static string ModeText(MonitorMode mode) => mode switch
    {
        MonitorMode.Auto => "自動",
        MonitorMode.Feed => "RSS / Atom",
        MonitorMode.FullPage => "ページ全体",
        MonitorMode.Text => "テキスト",
        MonitorMode.CssSelector => "CSS Selector",
        MonitorMode.XPath => "XPath",
        MonitorMode.Regex => "正規表現",
        _ => "不明"
    };
}
