using System.Diagnostics;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly SettingsStore _settingsStore;
    private readonly Database _database;
    private readonly SharedHttpFetcher _fetcher;
    private readonly MonitorEngine _engine;
    private readonly OneShotScheduler _scheduler;
    private readonly NotificationService _notifications;
    private readonly UpdateDialogController _dialogs;
    private readonly SoundService _sound;
    private readonly FontZoomMessageFilter _fontZoom;
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private MainForm? _mainForm;
    private AppSettings _settings;
    private bool _exiting;
    private bool _configurationBusy;
    private readonly Queue<CheckResult> _deferredChecks = new();

    public TrayApplicationContext(AppPaths paths, SingleInstanceCoordinator single, bool autostart)
    {
        _paths = paths;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _logger = new FileLogger(paths.LogsDirectory);
        _sound = new SoundService(_logger);
        _logger.Info("起動");
        _settingsStore = new SettingsStore(paths.SettingsPath);
        _settings = _settingsStore.Load();
        UiFontManager.Initialize(_settings);
        _settingsStore.Save(_settings);
        if (_settingsStore.RecoveryMessage is not null) _logger.Error(_settingsStore.RecoveryMessage);
        _logger.Cleanup(_settings.LogRetentionDays);
        _database = new Database(paths.DatabasePath);
        _database.Initialize();
        _database.CleanupHistory(_settings.HistoryRetentionDays, DateTimeOffset.Now);
        _fetcher = new SharedHttpFetcher();
        _engine = new MonitorEngine(_database, _fetcher, _logger, () => _settings.NotificationsEnabled);
        _dialogs = new UpdateDialogController(_database, _logger, OpenUrl, _ui);
        _notifications = new NotificationService(_logger);
        ApplyWindowsIntegration(_settings);
        _scheduler = new OneShotScheduler(_database, _engine);
        _scheduler.CheckCompleted += result => _ui.Post(_ => HandleCompleted(result), null);
        _scheduler.BackgroundError += ex => _logger.Error("スケジューラーのバックグラウンド処理に失敗しました", ex);
        _scheduler.Start();
        _fontZoom = new FontZoomMessageFilter(() => _settings, SaveSettings);
        single.ShowRequested += () => _ui.Post(_ => ShowMain(), null);

        var menu = new ContextMenuStrip();
        menu.Items.Add("WebSite Monitorを開く", null, (_, _) => ShowMain());
        menu.Items.Add("全て今すぐ確認", null, async (_, _) => await CheckAllAsync());
        menu.Items.Add("監視を一時停止", null, (_, _) => { _scheduler.TogglePause(); UpdateTrayMenu(menu); });
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add("設定をエクスポート", null, async (_, _) => await TransferSettingsAsync(false));
        menu.Items.Add("設定をインポート", null, async (_, _) => await TransferSettingsAsync(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        UiFontManager.Register(menu, _settings.UiFontSize);
        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "WebSite Monitor",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        if (!autostart) ShowMain();
        _ui.Post(_ => _dialogs.TryShowNext(), null);
    }

    private void ApplyWindowsIntegration(AppSettings settings)
    {
        if (!settings.WindowsIntegrationEnabled)
        {
            settings.StartWithWindows = false;
            WindowsIntegration.Remove(Application.ExecutablePath);
            return;
        }
        WindowsIntegration.Initialize(Application.ExecutablePath, _logger);
        WindowsIntegration.SetAutoStart(settings.StartWithWindows, Application.ExecutablePath);
    }

    private void UpdateTrayMenu(ContextMenuStrip menu)
    {
        if (menu.Items.Count > 2) menu.Items[2].Text = _scheduler.Paused ? "監視を再開" : "監視を一時停止";
        _mainForm?.RefreshStatus();
    }

    public void ShowMain()
    {
        if (_configurationBusy) return;
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_database, _engine, _scheduler, _sound, () => _settings, SaveSettings, ShowSettings, OpenUrl, TransferSettingsAsync, () => _configurationBusy);
            _mainForm.FormClosed += (_, _) => { _mainForm?.Dispose(); _mainForm = null; };
        }
        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized) _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void ShowSettings()
    {
        if (_configurationBusy) return;
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(_mainForm) != DialogResult.OK) return;
        _settings = form.Value;
        ApplyWindowsIntegration(_settings);
        SaveSettings(_settings);
    }

    private void SaveSettings(AppSettings settings)
    {
        if (_configurationBusy) return;
        _settings = settings;
        _settingsStore.Save(settings);
    }

    private async Task CheckAllAsync()
    {
        try { await _scheduler.CheckNowAsync(_database.GetSites().Where(site => site.Enabled)); }
        catch (Exception ex) { _logger.Error("一括確認に失敗しました", ex); }
    }

    private void HandleCompleted(CheckResult result)
    {
        if (_exiting) return;
        if (_configurationBusy) { _deferredChecks.Enqueue(result); return; }
        _mainForm?.Reload();
        _dialogs.TryShowNext();
        if (!result.ShouldNotify || result.NewHash is null || !_settings.NotificationsEnabled) return;
        var notificationUrl = NotificationUrl.ForEvent(result);
        var handled = false;
        if (result.Site.UpdateDialogNotification) handled = true;
        if (_settings.WindowsIntegrationEnabled && NotificationPolicy.ShouldDispatch(_settings.NotificationsEnabled, result.Site.WindowsNotification))
        {
            _notifications.Show(result.Site, notificationUrl);
            handled = true;
        }
        if (NotificationPolicy.ShouldDispatch(_settings.NotificationsEnabled, result.Site.PopupNotification))
        {
            new UpdatePopup(result.Site, OpenUrl, notificationUrl).Show();
            handled = true;
        }
        if (NotificationPolicy.ShouldDispatch(_settings.NotificationsEnabled, result.Site.SoundNotification) && !string.IsNullOrWhiteSpace(result.Site.SoundFile))
        {
            handled = true;
            _ = ObserveSoundAsync(result.Site);
        }
        if (handled) _database.MarkNotified(result.Site.Id, result.MonitorRevision, result.NewHash);
    }

    private async Task ObserveSoundAsync(Site site)
    {
        try
        {
            var result = await _sound.PlayAsync(ResolveSound(site.SoundFile!), site.SoundVolume, _settings).ConfigureAwait(false);
            if (result.IsFailure) _logger.Error($"通知音再生に失敗しました SiteId={site.Id}: {result.Message}", result.Exception ?? new InvalidOperationException(result.Message));
        }
        catch (Exception ex)
        {
            _logger.Error($"通知音再生に失敗しました SiteId={site.Id}", ex);
        }
    }
    private string ResolveSound(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_paths.SoundsDirectory, path);

    private void OpenUrl(string url)
    {
        var start = BrowserLaunch.CreateStartInfo(url);
        if (start is null) return;
        try { Process.Start(start); }
        catch (Exception ex) { _logger.Error("ブラウザーを開けませんでした。", ex); }
    }

    private void ExitApplication()
    {
        if (_configurationBusy) return;
        if (_exiting) return;
        _exiting = true;
        _dialogs.Dispose();
        _tray.Visible = false;
        _mainForm?.AllowExit();
        _mainForm?.Close();
        ExitThread();
    }

    private async Task TransferSettingsAsync(bool importing)
    {
        if (_configurationBusy || _exiting) return;
        if (Application.OpenForms.Cast<Form>().Any(f => f is SiteEditForm or SettingsForm))
        {
            MessageBox.Show("サイト編集・設定画面を閉じてから実行してください。", "設定入出力", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ShowMain();
        _configurationBusy = true;
        if (_mainForm is not null) _mainForm.Enabled = false;
        _tray.ContextMenuStrip!.Enabled = false;
        Application.RemoveMessageFilter(_fontZoom);
        _dialogs.ConfigurationSuspended = true;
        var activeDialog = _dialogs.Active;
        if (activeDialog is not null) activeDialog.Enabled = false;
        IDisposable? lease = null;
        try
        {
            string path;
            if (importing)
            {
                using var open = new OpenFileDialog { Title = "設定をインポート", Filter = "WebSite Monitor設定 (*.wsmcfg)|*.wsmcfg", CheckFileExists = true, Multiselect = false };
                if (open.ShowDialog(_mainForm) != DialogResult.OK) return;
                path = open.FileName;
            }
            else
            {
                using var save = new SaveFileDialog { Title = "設定をエクスポート", Filter = "WebSite Monitor設定 (*.wsmcfg)|*.wsmcfg", DefaultExt = "wsmcfg", AddExtension = true,
                    FileName = $"WebSiteMonitor_Settings_{DateTime.Now:yyyy-MM-dd}.wsmcfg", OverwritePrompt = true };
                if (save.ShowDialog(_mainForm) != DialogResult.OK) return;
                path = save.FileName;
            }
            using var password = new ConfigurationPasswordDialog(!importing, _settings.UiFontSize);
            if (password.ShowDialog(_mainForm) != DialogResult.OK) return;
            var secret = password.Password;
            if (!importing)
            {
                var snapshot = ConfigurationTransfer.Capture(_database.GetSites(), _settings);
                await Task.Run(() => ConfigurationTransfer.Export(path, snapshot, secret));
                MessageBox.Show(_mainForm, $"{snapshot.Sites.Count}件のサイト設定とアプリ全体の設定を暗号化して保存しました。", "設定をエクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var document = await Task.Run(() => ConfigurationTransfer.Read(path, secret));
            using var mode = new ConfigurationImportDialog(document.Sites.Count, _settings.UiFontSize);
            if (mode.ShowDialog(_mainForm) != DialogResult.OK) return;
            var importMode = mode.Mode;
            lease = await _scheduler.SuspendForConfigurationAsync();
            var result = await Task.Run(() => ConfigurationTransfer.Import(document, importMode, _database, _settingsStore));
            _settings = document.AppSettings;
            UiFontManager.Initialize(_settings);
            foreach (Form form in Application.OpenForms) UiFontManager.Apply(form, _settings.UiFontSize);
            _mainForm?.ApplyImportedSettings();
            // Storage is already committed. External Windows registration failures must not
            // pretend to roll back a successful settings import.
            var integrationWarning = false;
            try { ApplyWindowsIntegration(_settings); } catch { integrationWarning = true; }
            var message = $"{result.ImportedSites}件のサイト設定とアプリ全体の設定を取り込みました。";
            if (result.MissingSoundFiles != 0) message += $"\n見つからない絶対パスの音源：{result.MissingSoundFiles}件。音源を指定し直してください。監視は継続します。";
            if (integrationWarning) message += "\nWindows統合の反映を確認してください。設定データの取り込みは完了しています。";
            MessageBox.Show(_mainForm, message, "設定をインポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ConfigurationException ex)
        {
            // All ConfigurationException messages are fixed product messages, never raw input.
            MessageBox.Show(_mainForm, ex.Message, "設定入出力", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            _logger.Error("設定入出力を中止しました。");
            MessageBox.Show(_mainForm, "設定入出力を中止しました。保存先、ファイル、監視処理の状態を確認してください。", "設定入出力", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            lease?.Dispose();
            _configurationBusy = false;
            if (_mainForm is not null && !_mainForm.IsDisposed) _mainForm.Enabled = true;
            if (activeDialog is not null && !activeDialog.IsDisposed) activeDialog.Enabled = true;
            _tray.ContextMenuStrip!.Enabled = true;
            Application.AddMessageFilter(_fontZoom);
            _dialogs.ConfigurationSuspended = false;
            while (_deferredChecks.TryDequeue(out var result)) HandleCompleted(result);
            _dialogs.TryShowNext();
        }
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _dialogs.Dispose();
        _logger.Info("終了開始");
        try { _scheduler.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.Error("スケジューラー終了待機に失敗しました", ex); }
        _fetcher.Dispose();
        _fontZoom.Dispose();
        _sound.Dispose();
        _tray.Dispose();
        _logger.Info("終了完了");
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting) ExitApplication();
        base.Dispose(disposing);
    }
}
