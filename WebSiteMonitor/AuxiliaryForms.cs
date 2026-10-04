using System.Reflection;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class HistoryForm : Form
{
    private readonly Database _database;
    private readonly DataGridView _grid;
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, IntegralHeight = false, MaxDropDownItems = 15 };
    private readonly Label _message = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(8) };
    private readonly Button _browser = new() { Text = "Webサイトを開く", AutoSize = true };
    private readonly ToolTip _filterTip = new();
    private int _loadGeneration;
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;
    internal long? SelectedSiteId => (_filter.SelectedItem as HistoryChoice)?.SiteId;
    private sealed record HistoryChoice(long? SiteId, string Caption) { public override string ToString() => Caption; }

    public HistoryForm(Database database, long? siteId, Action<string> open)
    {
        _database = database;
        Text = "WebSite Monitor — 更新履歴";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        MinimumSize = new Size(900, 520);
        Size = new Size(1160, 650);
        StartPosition = FormStartPosition.CenterParent;
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false, ScrollBars = ScrollBars.Both,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
        };
        _grid = grid;
        grid.MultiSelect = false;
        AddColumn(grid, "At", "日時", 150);
        AddColumn(grid, "Name", "サイト名", 170);
        AddColumn(grid, "Old", "変更前プレビュー", 280);
        AddColumn(grid, "New", "変更後プレビュー", 280);
        AddColumn(grid, "Url", "URL", 250);
        var filterPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.Controls.Add(new Label { Text = "表示対象", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 12, 4) }, 0, 0);
        filterPanel.Controls.Add(_filter, 1, 0);
        _filter.Items.Add(new HistoryChoice(null, "すべて"));
        foreach (var site in database.GetSites())
            _filter.Items.Add(new HistoryChoice(site.Id, $"{DisplayText.Content(site.Name)} — {NotificationUrl.Sanitize(site.Url)}（ID:{site.Id}）"));
        _filter.SelectedIndex = 0;
        if (siteId is not null)
            for (var i = 1; i < _filter.Items.Count; i++)
                if (((HistoryChoice)_filter.Items[i]!).SiteId == siteId) { _filter.SelectedIndex = i; break; }
        void SizeDropDown()
        {
            var required = _filter.Items.Cast<object>().Select(item => TextRenderer.MeasureText(item.ToString(), _filter.Font).Width + 28).DefaultIfEmpty(200).Max();
            _filter.DropDownWidth = Math.Clamp(required, Math.Max(1, _filter.Width), Math.Max(_filter.Width, Screen.FromControl(this).WorkingArea.Width - 32));
            _filterTip.SetToolTip(_filter, _filter.SelectedItem?.ToString());
        }
        _filter.FontChanged += (_, _) => SizeDropDown();
        _filter.DropDown += (_, _) => SizeDropDown();
        _filter.SelectedIndexChanged += (_, _) => { SizeDropDown(); if (IsHandleCreated) CurrentLoad = LoadHistoryAsync(); };
        Shown += (_, _) => CurrentLoad = LoadHistoryAsync();
        FormClosed += (_, _) => { _loadGeneration++; _filterTip.Dispose(); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        var browser = _browser;
        grid.SelectionChanged += (_, _) => UpdateBrowserState();
        browser.Click += (_, _) =>
        {
            if (grid.CurrentRow?.Tag is not HistoryEntry entry) return;
            var safe = NotificationUrl.Sanitize(entry.Url);
            if (safe.Length != 0) { try { open(safe); } catch { } }
        };
        UpdateBrowserState();
        buttons.Controls.AddRange([close, browser]);
        Controls.Add(grid);
        Controls.Add(_message);
        Controls.Add(buttons);
        Controls.Add(filterPanel);
        CancelButton = close;
        UiFontManager.Apply(this, UiFontManager.CurrentSize);
    }

    private void UpdateBrowserState() => _browser.Enabled = _grid.CurrentRow?.Tag is HistoryEntry entry && NotificationUrl.Sanitize(entry.Url).Length != 0;

    private async Task LoadHistoryAsync()
    {
        var generation = ++_loadGeneration;
        var id = SelectedSiteId;
        _grid.Rows.Clear();
        _message.Text = "更新履歴を読み込み中…";
        try
        {
            // The SQL applies SiteId before LIMIT; never filter the global latest 1000 in the UI.
            var entries = await Task.Run(() => _database.GetHistory(id));
            if (IsDisposed || Disposing || generation != _loadGeneration) return;
            _grid.SuspendLayout();
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            try
            {
                foreach (var entry in entries)
                {
                    var row = _grid.Rows[_grid.Rows.Add(entry.ChangedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), DisplayText.Content(entry.SiteName),
                        DisplayText.Content(entry.OldPreview), DisplayText.Content(entry.NewPreview), NotificationUrl.Sanitize(entry.Url))];
                    row.Tag = entry;
                }
            }
            finally { _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; _grid.ResumeLayout(); }
            UpdateBrowserState();
            _message.Text = entries.Count == 0 ? (id is null ? "更新履歴はありません。" : "このサイトの更新履歴はありません。") : $"最新 {entries.Count}件を表示しています。";
        }
        catch (Exception ex)
        { if (!IsDisposed && !Disposing && generation == _loadGeneration) _message.Text = "更新履歴を読み込めませんでした。 " + DisplayText.Exception(ex); }
    }

    private static void AddColumn(DataGridView grid, string name, string header, int width)
        => grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.Automatic });
}

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _integration = new() { Text = "Windows通知・スタートメニュー統合を有効にする", AutoSize = true };
    private readonly CheckBox _start = new() { Text = "Windows起動時に自動起動", AutoSize = true };
    private readonly CheckBox _notifications = new() { Text = "更新通知を有効にする", AutoSize = true };
    private readonly CheckBox _popup = new() { Text = "新規サイトの既定値として独自ポップアップを使用", AutoSize = true };
    private readonly NumericUpDown _history = new() { Minimum = 1, Maximum = 3650, ThousandsSeparator = true };
    private readonly NumericUpDown _logs = new() { Minimum = 1, Maximum = 365, ThousandsSeparator = true };
    private readonly ComboBox _pcmRate = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _pcmBits = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _pcmChannels = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public AppSettings Value { get; private set; }

    public SettingsForm(AppSettings value)
    {
        Value = value;
        Text = "WebSite Monitor — 設定";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 300);
        Size = new Size(760, 700);
        _pcmRate.Items.AddRange([8000, 11025, 16000, 22050, 32000, 44100, 48000, 96000]);
        _pcmBits.Items.AddRange([8, 16, 24, 32]);
        _pcmChannels.Items.AddRange([new ChannelChoice(1, "モノラル（1ch）"), new ChannelChoice(2, "ステレオ（2ch）")]);
        _integration.Checked = value.WindowsIntegrationEnabled;
        _start.Checked = value.StartWithWindows;
        _notifications.Checked = value.NotificationsEnabled;
        _popup.Checked = value.DefaultPopup;
        _history.Value = Math.Clamp(value.HistoryRetentionDays, 1, 3650);
        _logs.Value = Math.Clamp(value.LogRetentionDays, 1, 365);
        SelectOrAdd(_pcmRate, value.PcmSampleRate);
        SelectOrAdd(_pcmBits, value.PcmBits);
        _pcmChannels.SelectedItem = _pcmChannels.Items.Cast<ChannelChoice>().First(x => x.Value == Math.Clamp(value.PcmChannels, 1, 2));

        var root = new TableLayoutPanel { ColumnCount = 2, Padding = new Padding(18) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, "Windows統合", _integration);
        AddRow(root, "", _start);
        AddRow(root, "通知", _notifications);
        AddRow(root, "ポップアップ", _popup);
        AddRow(root, "履歴保存日数", _history);
        AddRow(root, "ログ保存日数", _logs);
        var pcm = new GroupBox { Text = "PCM（.pcm / .raw）再生設定", Dock = DockStyle.Top, AutoSize = true };
        var pcmTable = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
        pcmTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pcmTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(pcmTable, "サンプルレート", _pcmRate);
        AddRow(pcmTable, "ビット深度", _pcmBits);
        AddRow(pcmTable, "チャンネル", _pcmChannels);
        pcm.Controls.Add(pcmTable);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(pcm, 0, root.RowCount);
        root.SetColumnSpan(pcm, 2);
        root.RowCount++;
        var integration = new GroupBox { Text = "Windows統合設定", Dock = DockStyle.Top, AutoSize = true };
        var integrationFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var remove = new Button { Text = "Windows統合情報を解除", AutoSize = true };
        remove.Click += DisableIntegration;
        integrationFlow.Controls.Add(new Label { Text = "解除すると通知用登録・スタートメニューショートカット・自動起動を削除します。dataは削除しません。", AutoSize = true, MaximumSize = new Size(620, 0) });
        integrationFlow.SetFlowBreak(integrationFlow.Controls[0], true);
        integrationFlow.Controls.Add(remove);
        integration.Controls.Add(integrationFlow);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(integration, 0, root.RowCount);
        root.SetColumnSpan(integration, 2);
        root.RowCount++;
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var about = new Button { Text = "バージョン情報 / 第三者ライセンス", AutoSize = true };
        save.Click += Save;
        about.Click += (_, _) => { using var dialog = new AboutForm(); dialog.ShowDialog(this); };
        actions.Controls.AddRange([save, cancel, about]);
        ScrollableDialogLayout.Install(this, root, actions, new Size(680, 300));
        _integration.CheckedChanged += (_, _) => _start.Enabled = _integration.Checked;
        _start.Enabled = _integration.Checked;
        AcceptButton = save;
        CancelButton = cancel;
        UiFontManager.Apply(this, Value.UiFontSize);
    }

    private sealed record ChannelChoice(int Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0) table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, table.RowCount);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, label.Length > 0 ? 1 : 0, table.RowCount);
        if (label.Length == 0) table.SetColumnSpan(control, 2);
        table.RowCount++;
    }

    private static void SelectOrAdd(ComboBox box, int value)
    {
        if (!box.Items.Contains(value)) box.Items.Add(value);
        box.SelectedItem = value;
    }

    private void DisableIntegration(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Windows統合を無効にし、通知用登録・ショートカット・自動起動を解除します。dataは削除しません。", "Windows統合の解除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _integration.Checked = false;
        _start.Checked = false;
    }

    private void Save(object? sender, EventArgs e)
    {
        Value.WindowsIntegrationEnabled = _integration.Checked;
        Value.StartWithWindows = _integration.Checked && _start.Checked;
        Value.NotificationsEnabled = _notifications.Checked;
        Value.DefaultPopup = _popup.Checked;
        Value.HistoryRetentionDays = (int)_history.Value;
        Value.LogRetentionDays = (int)_logs.Value;
        Value.PcmSampleRate = (int)(_pcmRate.SelectedItem ?? 44100);
        Value.PcmBits = (int)(_pcmBits.SelectedItem ?? 16);
        Value.PcmChannels = (_pcmChannels.SelectedItem as ChannelChoice)?.Value ?? 2;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "WebSite Monitor について";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 520);
        Size = new Size(860, 680);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "WebSite Monitor\nv" + ProductInfo.Version, Font = new Font("Yu Gothic UI", 13F, FontStyle.Bold), AutoSize = true }, 0, 0);
        var license = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = LicenseText.Load() };
        layout.Controls.Add(license, 0, 1);
        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        layout.Controls.Add(close, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        UiFontManager.Apply(this, UiFontManager.CurrentSize);
    }
}

internal static class LicenseText
{
    public static string Load()
    {
        const string resourceName = "WebSiteMonitor.Resources.THIRD_PARTY_NOTICES.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return "第三者ライセンス情報を読み込めませんでした。";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal static class PopupStackLayout
{
    public static IReadOnlyList<Rectangle> Calculate(Rectangle workArea, IReadOnlyList<Size> sizes, int margin = 12, int gap = 8)
    {
        var results = new Rectangle[sizes.Count];
        var x = workArea.Right - margin;
        var y = workArea.Bottom - margin;
        for (var index = sizes.Count - 1; index >= 0; index--)
        {
            var size = sizes[index];
            if (y - size.Height < workArea.Top + margin)
            {
                x -= size.Width + gap;
                y = workArea.Bottom - margin;
            }
            var left = Math.Max(workArea.Left + margin, x - size.Width);
            var top = Math.Max(workArea.Top + margin, y - size.Height);
            results[index] = new Rectangle(left, top, size.Width, size.Height);
            y = top - gap;
        }
        return results;
    }
}

internal static class PopupManager
{
    private static readonly object Sync = new();
    private static readonly List<UpdatePopup> Popups = [];

    public static void Register(UpdatePopup popup)
    {
        lock (Sync)
        {
            Popups.RemoveAll(form => form.IsDisposed);
            Popups.Add(popup);
            Reflow();
        }
    }

    public static void Remove(UpdatePopup popup)
    {
        lock (Sync)
        {
            Popups.Remove(popup);
            Reflow();
        }
    }

    private static void Reflow()
    {
        foreach (var group in Popups.Where(form => !form.IsDisposed).GroupBy(form => form.TargetScreen.DeviceName))
        {
            var forms = group.ToList();
            var workArea = forms[0].TargetScreen.WorkingArea;
            var rectangles = PopupStackLayout.Calculate(workArea, forms.Select(form => form.Size).ToArray());
            for (var index = 0; index < forms.Count; index++) forms[index].Location = rectangles[index].Location;
        }
    }
}

internal sealed class UpdatePopup : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };
    internal Screen TargetScreen { get; }
    internal string NotificationTargetUrl { get; }

    public UpdatePopup(Site site, Action<string> open, string? notificationUrl = null)
    {
        NotificationTargetUrl = NotificationUrl.Sanitize(notificationUrl ?? NotificationUrl.ForSite(site));
        Text = "WebSite Monitor";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(390, 165);
        Size = new Size(420, 180);
        TargetScreen = Screen.FromPoint(Cursor.Position);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var message = new Label { Text = $"{DisplayText.Content(site.Name)}\n更新を検出しました\n{DateTime.Now:yyyy/MM/dd HH:mm}", AutoSize = true, Dock = DockStyle.Fill };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "閉じる", AutoSize = true };
        var openButton = new Button { Text = "Webサイトを開く", AutoSize = true, Enabled = NotificationTargetUrl.Length != 0 };
        openButton.Click += (_, _) =>
        {
            var safe = NotificationUrl.Sanitize(NotificationTargetUrl);
            if (safe.Length == 0) return;
            try { open(safe); Close(); } catch { }
        };
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange([close, openButton]);
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        _timer.Tick += (_, _) => Close();
        Shown += (_, _) => { PopupManager.Register(this); _timer.Start(); };
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); PopupManager.Remove(this); };
        UiFontManager.Apply(this, UiFontManager.CurrentSize);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }
}
