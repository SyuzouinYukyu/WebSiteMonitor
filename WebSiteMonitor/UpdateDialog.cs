using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class UpdateDialog : Form
{
    private readonly Func<bool> _acknowledge;
    private readonly Action<string> _open;
    private readonly LinkLabel _link;
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    private bool _captionGesture;
    private bool _applicationShutdown;
    public bool Confirmed { get; private set; }
    public PendingUpdateDialog Notification { get; }

    public UpdateDialog(PendingUpdateDialog notification, Func<bool> acknowledge, Action<string> open)
    {
        Notification = notification with { Url = NotificationUrl.Sanitize(notification.Url) };
        _acknowledge = acknowledge;
        _open = open;
        Text = "WebSite Monitor — 更新通知";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 200);
        ClientSize = new Size(560, 210);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(18) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var message = new Label { Text = "ウェブサイトが更新されました。", AutoSize = true, Margin = new Padding(0, 0, 0, 14) };
        var links = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _link = new LinkLabel { Text = Notification.Url.Length == 0 ? "安全な通知先URLがありません。" : Notification.Url,
            AutoSize = true, Margin = Padding.Empty, TabStop = Notification.Url.Length != 0, Enabled = Notification.Url.Length != 0 };
        _link.LinkClicked += (_, _) => OpenLink();
        links.Controls.Add(_link);
        void Reflow()
        {
            _link.MaximumSize = new Size(Math.Max(100, links.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4), 0);
            message.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
            _error.MaximumSize = message.MaximumSize;
        }
        links.ClientSizeChanged += (_, _) => Reflow();
        FontChanged += (_, _) => Reflow();
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(links, 0, 1);
        layout.Controls.Add(_error, 0, 2);
        Controls.Add(layout);
        UiFontManager.Apply(this, UiFontManager.CurrentSize);
        Reflow();
    }

    protected override bool ShowWithoutActivation => true;

    internal void OpenLink()
    {
        var safe = NotificationUrl.Sanitize(Notification.Url);
        if (safe.Length == 0) return;
        try { _open(safe); }
        catch { ShowError("ブラウザーを開けませんでした。"); }
    }

    internal bool ConfirmFromCaption()
    {
        try
        {
            if (!_acknowledge()) { ShowError("確認を保存できません。もう一度「×」を押してください。"); return false; }
            Confirmed = true;
            return true;
        }
        catch { ShowError("確認を保存できません。もう一度「×」を押してください。"); return false; }
    }

    private void ShowError(string message) { _error.Text = message; _error.Visible = true; }

    internal void Shutdown()
    {
        _applicationShutdown = true;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.Enter || keyData == (Keys.Alt | Keys.F4)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        const int WmNcLButtonDown = 0x00A1, HitClose = 20, WmSysCommand = 0x0112, ScClose = 0xF060;
        if (m.Msg == WmNcLButtonDown && m.WParam.ToInt32() == HitClose)
        {
            // DefWindowProc sends SC_CLOSE while handling the actual caption-button gesture.
            // Scope the permission to this call so an abandoned mouse gesture cannot authorize Alt+F4 later.
            _captionGesture = true;
            try { base.WndProc(ref m); }
            finally { _captionGesture = false; }
            return;
        }
        if (m.Msg == WmSysCommand && (m.WParam.ToInt64() & 0xFFF0) == ScClose && !_applicationShutdown)
        {
            if (!_captionGesture || !ConfirmFromCaption()) return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!Confirmed && !_applicationShutdown && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing))
            e.Cancel = true;
        base.OnFormClosing(e);
    }
}

internal sealed class UpdateDialogController : IDisposable
{
    private readonly Database _database;
    private readonly FileLogger _logger;
    private readonly Action<string> _open;
    private readonly SynchronizationContext _ui;
    private bool _stopping;
    private bool _suspended;
    internal bool ConfigurationSuspended { get; set; }
    internal UpdateDialog? Active { get; private set; }

    public UpdateDialogController(Database database, FileLogger logger, Action<string> open, SynchronizationContext ui)
    { _database = database; _logger = logger; _open = open; _ui = ui; }

    public void TryShowNext()
    {
        if (_stopping || _suspended || ConfigurationSuspended || Active is not null) return;
        try
        {
            var pending = _database.GetNextPendingUpdateDialog();
            if (pending is null) return;
            var dialog = new UpdateDialog(pending, () => Acknowledge(pending.Id), _open);
            Active = dialog;
            dialog.FormClosed += (_, _) =>
            {
                Active = null;
                var confirmed = dialog.Confirmed;
                dialog.Dispose();
                if (!confirmed) _suspended = true;
                if (confirmed && !_stopping) _ui.Post(_ => TryShowNext(), null);
            };
            dialog.Show();
        }
        catch
        {
            Active?.Dispose();
            Active = null;
            _logger.Error("更新ダイアログを表示できませんでした。未確認通知は次回起動時に復元します。");
        }
    }

    private bool Acknowledge(long id)
    {
        try { return _database.AcknowledgeUpdateDialog(id); }
        catch { _logger.Error("更新ダイアログの確認を保存できませんでした。"); return false; }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        Active?.Shutdown();
        Active?.Dispose();
        Active = null;
    }
}
