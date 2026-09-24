using WebSiteMonitor.Core;

namespace WebSiteMonitor;

/// <summary>Inspection-only entry point. It uses neither production portable data nor the application mutex.</summary>
internal static class UiProbeApplication
{
    public static void Run(TimeSpan? autoCloseAfter, int siteCount, double? fontSize = null, int? windowWidth = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "WebSiteMonitor-ui-probe-" + Guid.NewGuid().ToString("N"));
        var childForms = new List<Form>();
        try
        {
            var paths = AppPaths.CreateAndVerify(root);
            var logger = new FileLogger(paths.LogsDirectory);
            var settingsStore = new SettingsStore(paths.SettingsPath);
            var settings = settingsStore.Load();
            if (fontSize is not null) settings.UiFontSize = UiFontSettings.Clamp(fontSize.Value);
            if (windowWidth is not null) settings.WindowWidth = Math.Clamp(windowWidth.Value, 980, 3840);
            UiFontManager.Initialize(settings);
            var database = new Database(paths.DatabasePath);
            database.Initialize();
            var totalSites = Math.Clamp(siteCount, 1, 100);
            for (var index = 1; index <= totalSites; index++)
            {
                var current = new Site { Name = index == 1 ? "表示確認用サイト" : $"表示確認用サイト {index:000}", Url = "https://example.test/", MonitorMode = MonitorMode.Text, IntervalMinutes = 60, DailyTime = "09:00" };
                database.SaveSite(current);
            }
            var site = database.GetSites().First();
            database.ApplySuccess(site.Id, "UI-A", "初回表示", null, null, MonitorMode.Text, DateTimeOffset.Now);
            database.ApplySuccess(site.Id, "UI-B", "更新表示", null, null, MonitorMode.Text, DateTimeOffset.Now);

            using var fetcher = new SharedHttpFetcher();
            using var scheduler = new OneShotScheduler(database, new MonitorEngine(database, fetcher, logger));
            using var sound = new SoundService();
            void Save(AppSettings value) { settings = value; settingsStore.Save(value); }
            void ShowSettings()
            {
                using var dialog = new SettingsForm(settings);
                if (dialog.ShowDialog() == DialogResult.OK) Save(dialog.Value);
            }

            using var form = new MainForm(database, new MonitorEngine(database, fetcher, logger), scheduler, sound, () => settings, Save, ShowSettings, _ => { });
            form.Text = "WebSiteMonitor v1.0.4 — UI検査";
            form.AllowExit();
            form.Shown += (_, _) =>
            {
                childForms.Add(new SettingsForm(settings));
                childForms.Add(new SiteEditForm(null, new MonitorEngine(database, fetcher, logger), sound, settings, paths.SoundsDirectory));
                childForms.Add(new SiteEditForm(site, new MonitorEngine(database, fetcher, logger), sound, settings, paths.SoundsDirectory));
                childForms.Add(new HistoryForm(database, site.Id, _ => { }));
                childForms.Add(new AboutForm());
                foreach (var child in childForms) child.Show(form);
                new UpdatePopup(new Site { Name = "表示確認用ポップアップ", Url = "https://example.test/" }, _ => { }).Show(form);
                if (autoCloseAfter is not { } duration) return;
                var timer = new System.Windows.Forms.Timer { Interval = Math.Clamp((int)duration.TotalMilliseconds, 1000, 60000) };
                timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); form.Close(); };
                form.FormClosed += (_, _) => timer.Dispose();
                timer.Start();
            };
            Application.Run(form);
        }
        finally
        {
            foreach (var child in childForms) { try { child.Dispose(); } catch { } }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}