using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class ConfigurationPasswordDialog : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, MaxLength = 1024 };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, MaxLength = 1024 };
    public string Password => _password.Text;

    public ConfigurationPasswordDialog(bool exporting, double fontSize)
    {
        Text = exporting ? "設定をエクスポート — パスワード" : "設定をインポート — パスワード";
        Font = new Font("Yu Gothic UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(740, 420);
        Size = MinimumSize;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control control)
        {
            if (control is Label label) label.UseCompatibleTextRendering = false;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); control.Margin = new Padding(4, 8, 4, 8); table.Controls.Add(control);
        }
        Add(new Label { Text = exporting
            ? "8文字以上のパスワードを入力してください。\nエクスポート成功後、Windowsユーザー専用のDPAPIで暗号化して記憶します。\n過去のバックアップには作成時のパスワードが必要です。"
            : "8文字以上のパスワードを入力してください。\nこのファイルを暗号化したパスワードが必要です。入力した値は記憶しません。",
            AutoSize = true, MaximumSize = new Size(680, 0), Padding = new Padding(0, 0, 0, 8) });
        Add(new Label { Text = "パスワード", AutoSize = true });
        Add(_password);
        if (exporting) { Add(new Label { Text = "パスワード（確認）", AutoSize = true }); Add(_confirm); }
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), WrapContents = true };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "続行", AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (!ExportPasswordStore.IsValid(Password) || exporting && Password != _confirm.Text)
            { MessageBox.Show(this, "8文字以上で指定し、確認入力を一致させてください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        Controls.Add(table); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        UiFontManager.Apply(this, fontSize);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _password.Clear(); _confirm.Clear(); }
        base.Dispose(disposing);
    }
}

internal sealed class ConfigurationImportDialog : Form
{
    private readonly RadioButton _merge = new() { Text = "追加・統合（現在のサイトを維持）", Checked = true, AutoSize = true, UseCompatibleTextRendering = false };
    private readonly RadioButton _replace = new() { Text = "置換（取り込み元のサイト設定へ置換）", AutoSize = true, UseCompatibleTextRendering = false };
    public ConfigurationImportMode Mode => _merge.Checked ? ConfigurationImportMode.Merge : ConfigurationImportMode.Replace;

    public ConfigurationImportDialog(int count, double fontSize)
    {
        Text = "設定をインポート — 取り込み方式";
        Font = new Font("Yu Gothic UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(800, 500);
        Size = MinimumSize;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18) };
        layout.Controls.Add(new Label { Text = $"取り込み対象：{count}件のサイト設定", AutoSize = true });
        layout.Controls.Add(_merge); layout.Controls.Add(_replace);
        layout.Controls.Add(new Label { Text = "どちらの方式でもアプリ全体の設定を取り込み元へ置換します。\n自動起動・Windows統合・通知・表示設定も対象です。\n履歴と未確認通知は保持します。\n置換で参照を壊す場合は、変更せず中止します。", AutoSize = true, MaximumSize = new Size(720, 0), Padding = new Padding(0, 0, 0, 8) });
        foreach (Control control in layout.Controls)
        {
            if (control is Label label) label.UseCompatibleTextRendering = false;
            control.Margin = new Padding(4, 10, 4, 10);
        }
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), WrapContents = true };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "取り込む", AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (Mode == ConfigurationImportMode.Replace && MessageBox.Show(this,
                "現在のユーザー設定を取り込み元の設定へ置き換えます。\n履歴または未確認通知がある削除対象サイトが含まれる場合は中止します。\n置換しますか？",
                "設定の置換確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        Controls.Add(layout); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        UiFontManager.Apply(this, fontSize);
    }
}
