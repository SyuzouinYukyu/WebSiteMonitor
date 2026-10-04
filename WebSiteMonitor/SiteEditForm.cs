using System.Runtime.InteropServices;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class SiteEditForm : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly MonitorEngine _engine;
    private readonly SoundService _sound;
    private readonly AppSettings _settings;
    private readonly string _soundsDirectory;
    private readonly CheckBox _enabled = new() { Text = "このサイトを監視する", Checked = true, AutoSize = true };
    private readonly TextBox _name = new();
    private readonly TextBox _url = new();
    private readonly TextBox _notificationTargetUrl = new();
    private readonly CheckBox _browserUserAgent = new() { Text = "ブラウザー互換User-Agentを使用する", AutoSize = true };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _feed = new();
    private readonly TextBox _selector = new();
    private readonly TextBox _xpath = new();
    private readonly TextBox _regex = new();
    private readonly Label _feedLabel = new() { Text = "RSS / Atom URL", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _autoHint = new() { Text = "自動: ページからRSS / Atomを検出し、利用できない場合は本文テキストを監視します。", AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly ComboBox _schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _interval = new() { Minimum = 5, Maximum = 10080, Value = 60, ThousandsSeparator = true };
    private readonly DateTimePicker _daily = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    private readonly CheckBox _windows = new() { Text = "Windows通知", Checked = true, AutoSize = true };
    private readonly CheckBox _popup = new() { Text = "独自ポップアップ", AutoSize = true };
    private readonly CheckBox _updateDialog = new() { Text = "更新ダイアログ", Checked = true, AutoSize = true };
    private readonly CheckBox _soundOn = new() { Text = "通知サウンド", AutoSize = true };
    private readonly TextBox _soundFile = new();
    private readonly NumericUpDown _volume = new() { Minimum = 0, Maximum = 100, Value = 100 };
    private readonly Button _test = new() { Text = "監視テスト / 抽出プレビュー", AutoSize = true };
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _details = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 4) };
    private readonly Dictionary<Control, Label> _detailLabels = [];

    public Site Value { get; private set; }

    public SiteEditForm(Site? source, MonitorEngine engine, SoundService sound, AppSettings settings, string soundsDirectory)
    {
        _engine = engine;
        _sound = sound;
        _settings = settings;
        _soundsDirectory = soundsDirectory;
        Value = source is null ? new Site { PopupNotification = settings.DefaultPopup } : Copy(source);
        Text = source is null ? "WebSite Monitor — サイトの追加" : "WebSite Monitor — サイトの編集";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 760);
        Size = new Size(980, 880);

        _mode.Items.AddRange([
            new Choice<MonitorMode>(MonitorMode.Auto, "自動"),
            new Choice<MonitorMode>(MonitorMode.Feed, "RSS / Atom"),
            new Choice<MonitorMode>(MonitorMode.FullPage, "ページ全体"),
            new Choice<MonitorMode>(MonitorMode.Text, "テキスト"),
            new Choice<MonitorMode>(MonitorMode.CssSelector, "CSS Selector"),
            new Choice<MonitorMode>(MonitorMode.XPath, "XPath"),
            new Choice<MonitorMode>(MonitorMode.Regex, "正規表現")
        ]);
        _schedule.Items.AddRange([
            new Choice<ScheduleMode>(ScheduleMode.Interval, "一定間隔"),
            new Choice<ScheduleMode>(ScheduleMode.Daily, "毎日指定時刻"),
            new Choice<ScheduleMode>(ScheduleMode.Manual, "手動")
        ]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16), AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRootRow(root, "", _enabled);
        AddRootRow(root, "サイト名", FieldWithPaste(_name, false));
        AddRootRow(root, "URL", FieldWithPaste(_url, true));
        var notificationDestination = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        notificationDestination.Controls.Add(new Label { Text = "通知先URL（省略可）", AutoSize = true }, 0, 0);
        notificationDestination.Controls.Add(FieldWithPaste(_notificationTargetUrl, true), 0, 1);
        notificationDestination.Controls.Add(new Label { Text = "通知をクリックした際に開くURLです。認証付きRSS等では、トークンを含まない通常のページURLを指定してください。", AutoSize = true, MaximumSize = new Size(680, 0) }, 0, 2);
        AddRootRow(root, "", notificationDestination);
        AddRootRow(root, "", _browserUserAgent);
        AddRootRow(root, "", new Label { Text = "403 Forbidden等で通常取得できないサイト向け。必要な場合のみ有効にします。", AutoSize = true, MaximumSize = new Size(680, 0) });
        AddRootRow(root, "監視方式", _mode);
        BuildDetails();
        AddRootRow(root, "監視方式の詳細", _details);
        AddRootRow(root, "スケジュール", _schedule);
        AddRootRow(root, "確認間隔（分）", _interval);
        AddRootRow(root, "毎日の時刻", _daily);
        AddRootRow(root, "通知", BuildNotificationGroup());
        AddRootRow(root, "通知音", BuildSoundControls());
        AddRootRow(root, "音量（0～100）", _volume);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_test, 0, root.RowCount);
        root.SetColumnSpan(_test, 2);
        root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_preview, 0, root.RowCount);
        root.SetColumnSpan(_preview, 2);
        root.RowCount++;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "保存", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += Save;
        buttons.Controls.AddRange([save, cancel]);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(buttons, 0, root.RowCount);
        root.SetColumnSpan(buttons, 2);
        root.RowCount++;
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
        _mode.SelectedIndexChanged += (_, _) => UpdateInputs();
        _schedule.SelectedIndexChanged += (_, _) => UpdateInputs();
        _test.Click += async (_, _) => await TestAsync();
        LoadValues();
        UiFontManager.Apply(this, _settings.UiFontSize);
    }

    private void BuildDetails()
    {
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddDetail(_feedLabel, _feed);
        AddDetail(new Label { Text = "CSS Selector", AutoSize = true, Anchor = AnchorStyles.Left }, _selector);
        AddDetail(new Label { Text = "XPath", AutoSize = true, Anchor = AnchorStyles.Left }, _xpath);
        AddDetail(new Label { Text = "正規表現", AutoSize = true, Anchor = AnchorStyles.Left }, _regex);
        _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _details.Controls.Add(_autoHint, 0, _details.RowCount);
        _details.SetColumnSpan(_autoHint, 2);
        _details.RowCount++;
    }

    private void AddDetail(Label label, Control control)
    {
        _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _details.Controls.Add(label, 0, _details.RowCount);
        control.Dock = DockStyle.Fill;
        _details.Controls.Add(control, 1, _details.RowCount);
        _detailLabels[control] = label;
        _details.RowCount++;
    }

    private static void AddRootRow(TableLayoutPanel table, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0) table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 8) }, 0, table.RowCount);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(control, label.Length > 0 ? 1 : 0, table.RowCount);
        if (label.Length == 0) table.SetColumnSpan(control, 2);
        table.RowCount++;
    }

    private Control FieldWithPaste(TextBox textBox, bool isUrl)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var paste = new Button { Text = "貼り付け", AutoSize = true };
        paste.Click += (_, _) => PasteInto(textBox, isUrl);
        textBox.Dock = DockStyle.Fill;
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(paste, 1, 0);
        return panel;
    }

    private GroupBox BuildNotificationGroup()
    {
        var group = new GroupBox { Text = "更新を検出したときの通知", Dock = DockStyle.Top, AutoSize = true };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        panel.Controls.AddRange([_windows, _popup, _updateDialog, _soundOn]);
        group.Controls.Add(panel);
        return group;
    }

    private Control BuildSoundControls()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "参照", AutoSize = true };
        var play = new Button { Text = "▶ テスト再生", AutoSize = true };
        var stop = new Button { Text = "停止", AutoSize = true };
        browse.Click += BrowseSound;
        play.Click += async (_, _) => await PlaySoundAsync();
        stop.Click += (_, _) => _sound.StopTestPlayback();
        _soundFile.Dock = DockStyle.Fill;
        panel.Controls.Add(_soundFile, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        panel.Controls.Add(play, 2, 0);
        panel.Controls.Add(stop, 3, 0);
        return panel;
    }

    private void LoadValues()
    {
        _enabled.Checked = Value.Enabled;
        _name.Text = Value.Name;
        _url.Text = Value.Url;
        _notificationTargetUrl.Text = Value.NotificationTargetUrl ?? "";
        _browserUserAgent.Checked = Value.UseBrowserCompatibleUserAgent;
        SelectChoice(_mode, Value.MonitorMode);
        _feed.Text = Value.FeedUrl ?? "";
        _selector.Text = Value.Selector ?? "";
        _xpath.Text = Value.XPath ?? "";
        _regex.Text = Value.Regex ?? "";
        SelectChoice(_schedule, Value.ScheduleMode);
        _interval.Value = Math.Clamp(Value.IntervalMinutes, 5, 10080);
        if (TimeOnly.TryParse(Value.DailyTime, out var time)) _daily.Value = DateTime.Today.Add(time.ToTimeSpan());
        _windows.Checked = Value.WindowsNotification;
        _popup.Checked = Value.PopupNotification;
        _updateDialog.Checked = Value.UpdateDialogNotification;
        _soundOn.Checked = Value.SoundNotification;
        _soundFile.Text = Value.SoundFile ?? "";
        _volume.Value = Math.Clamp(Value.SoundVolume, 0, 100);
        UpdateInputs();
    }

    private static void SelectChoice<T>(ComboBox box, T value) where T : notnull
    {
        box.SelectedItem = box.Items.Cast<Choice<T>>().FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value));
    }

    private MonitorMode SelectedMode => (_mode.SelectedItem as Choice<MonitorMode>)?.Value ?? MonitorMode.Auto;
    private ScheduleMode SelectedSchedule => (_schedule.SelectedItem as Choice<ScheduleMode>)?.Value ?? ScheduleMode.Interval;

    private Site ReadValues()
    {
        Value.Name = _name.Text.Trim();
        Value.Url = ClipboardText.NormalizeUrl(_url.Text);
        Value.NotificationTargetUrl = Null(_notificationTargetUrl.Text);
        Value.Enabled = _enabled.Checked;
        Value.UseBrowserCompatibleUserAgent = _browserUserAgent.Checked;
        Value.MonitorMode = SelectedMode;
        Value.FeedUrl = SelectedMode == MonitorMode.Feed ? Null(_feed.Text) : null;
        Value.Selector = Null(_selector.Text);
        Value.XPath = Null(_xpath.Text);
        Value.Regex = Null(_regex.Text);
        Value.ScheduleMode = SelectedSchedule;
        Value.IntervalMinutes = (int)_interval.Value;
        Value.DailyTime = _daily.Value.ToString("HH:mm");
        Value.WindowsNotification = _windows.Checked;
        Value.PopupNotification = _popup.Checked;
        Value.UpdateDialogNotification = _updateDialog.Checked;
        Value.SoundNotification = _soundOn.Checked;
        Value.SoundFile = Null(_soundFile.Text);
        Value.SoundVolume = (int)_volume.Value;
        return Value;
    }

    private void UpdateInputs()
    {
        var mode = SelectedMode;
        SetDetailVisible(_feed, mode == MonitorMode.Feed);
        SetDetailVisible(_selector, mode == MonitorMode.CssSelector);
        SetDetailVisible(_xpath, mode == MonitorMode.XPath);
        SetDetailVisible(_regex, mode == MonitorMode.Regex);
        _autoHint.Visible = mode == MonitorMode.Auto;
        _interval.Enabled = SelectedSchedule == ScheduleMode.Interval;
        _daily.Enabled = SelectedSchedule == ScheduleMode.Daily;
    }

    private void SetDetailVisible(Control control, bool visible)
    {
        control.Visible = visible;
        _detailLabels[control].Visible = visible;
    }

    private void PasteInto(TextBox target, bool isUrl)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            target.Text = isUrl ? ClipboardText.NormalizeUrl(Clipboard.GetText()) : ClipboardText.NormalizeSiteName(Clipboard.GetText());
            target.Focus();
            target.SelectionStart = target.TextLength;
        }
        catch (ExternalException)
        {
            MessageBox.Show(this, "クリップボードを読み取れませんでした。もう一度お試しください。", "貼り付け", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task TestAsync()
    {
        try
        {
            _test.Enabled = false;
            _preview.Text = "取得中…";
            var result = await _engine.TestExtractAsync(ReadValues(), CancellationToken.None);
            _preview.Text = $"実際の監視方式: {MainForm.ModeText(result.EffectiveMode)}\r\n\r\n{NotificationUrl.RedactText(ContentHasher.Preview(result.Content, 12000))}";
        }
        catch (Exception ex)
        {
            _preview.Text = "エラー: " + NotificationUrl.RedactText(ex.Message);
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private void Save(object? sender, EventArgs e)
    {
        try
        {
            SiteValidation.Validate(ReadValues());
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(_notificationTargetUrl.Text) && NotificationUrl.Sanitize(_notificationTargetUrl.Text) != _notificationTargetUrl.Text.Trim())
                _notificationTargetUrl.Focus();
            else FocusInvalidField();
            MessageBox.Show(this, NotificationUrl.RedactText(ex.Message), "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void FocusInvalidField()
    {
        var control = SelectedMode switch
        {
            MonitorMode.Feed => _feed,
            MonitorMode.CssSelector => _selector,
            MonitorMode.XPath => _xpath,
            MonitorMode.Regex => _regex,
            _ => _url
        };
        control.Focus();
    }

    private void BrowseSound(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = SoundService.FileDialogFilter, Title = "通知音を選択" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Directory.CreateDirectory(_soundsDirectory);
            var baseName = Path.GetFileNameWithoutExtension(dialog.FileName);
            var extension = Path.GetExtension(dialog.FileName);
            var destination = Path.Combine(_soundsDirectory, baseName + extension);
            for (var index = 2; File.Exists(destination) && !FilesEqual(dialog.FileName, destination); index++) destination = Path.Combine(_soundsDirectory, $"{baseName} ({index}){extension}");
            if (!File.Exists(destination)) File.Copy(dialog.FileName, destination, false);
            _soundFile.Text = Path.GetFileName(destination);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "通知音をコピーできません: " + NotificationUrl.RedactText(ex.Message), "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlaySoundAsync()
    {
        var path = _soundFile.Text;
        if (!Path.IsPathRooted(path)) path = Path.Combine(_soundsDirectory, path);
        var result = await _sound.PlayTestAsync(path, (int)_volume.Value, _settings);
        if (result.IsFailure)
            MessageBox.Show(this, result.Message, "再生エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static bool FilesEqual(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return System.Security.Cryptography.SHA256.HashData(a).SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Site Copy(Site site) => new()
    {
        Id = site.Id, MonitorRevision = site.MonitorRevision, Name = site.Name, Url = site.Url, NotificationTargetUrl = site.NotificationTargetUrl, Enabled = site.Enabled, UseBrowserCompatibleUserAgent = site.UseBrowserCompatibleUserAgent, MonitorMode = site.MonitorMode,
        FeedUrl = site.FeedUrl, AutoDetectedFeedUrl = site.AutoDetectedFeedUrl, Selector = site.Selector, XPath = site.XPath, Regex = site.Regex,
        ScheduleMode = site.ScheduleMode, IntervalMinutes = site.IntervalMinutes, DailyTime = site.DailyTime,
        WindowsNotification = site.WindowsNotification, PopupNotification = site.PopupNotification, SoundNotification = site.SoundNotification,
        UpdateDialogNotification = site.UpdateDialogNotification,
        SoundFile = site.SoundFile, SoundVolume = site.SoundVolume, LastHash = site.LastHash, LastPreview = site.LastPreview,
        LastETag = site.LastETag, LastModified = site.LastModified, LastChecked = site.LastChecked, LastChanged = site.LastChanged,
        LastNotifiedHash = site.LastNotifiedHash, NextDue = site.NextDue, ConsecutiveErrors = site.ConsecutiveErrors,
        LastError = site.LastError, EffectiveMode = site.EffectiveMode
    };
}

internal static class ClipboardText
{
    public static string NormalizeSiteName(string value) => (value ?? string.Empty).TrimEnd('\r', '\n');
    public static string NormalizeUrl(string value) => new string((value ?? string.Empty).Where(ch => ch is not '\r' and not '\n').ToArray()).Trim();
}
