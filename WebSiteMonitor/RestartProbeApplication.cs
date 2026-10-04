using System.Diagnostics;
using System.Net;
using System.Reflection;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

// Like --ui-probe, this diagnostic uses only a new temporary directory and an isolated
// Mutex/pipe. It exercises the real menu callbacks, shutdown and same-EXE helper.
internal static class RestartProbeApplication
{
    internal const string ArgumentPrefix = "--restart-probe=";
    private static bool ValidRoot(string root) => string.Equals(Path.GetDirectoryName(root), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("WebSiteMonitor-restart-probe-", StringComparison.Ordinal);

    internal static void ReportHelperFailure(string argument, RestartLauncher.WaitResult result)
    {
        var root = Path.GetFullPath(argument[ArgumentPrefix.Length..]);
        if (ValidRoot(root)) File.WriteAllText(Path.Combine(root, "FAILED"), "helper wait: " + result);
    }
    internal static void Run(string argument)
    {
        var root = Path.GetFullPath(argument[ArgumentPrefix.Length..]);
        if (!ValidRoot(root)) return;
        var second = File.Exists(Path.Combine(root, "old-process.txt"));
        var closeOnly = File.Exists(Path.Combine(root, "close"));
        var repeatsPath = Path.Combine(root, "repeats");
        var repeats = File.Exists(repeatsPath) ? Math.Clamp(int.Parse(File.ReadAllText(repeatsPath)), 1, 20) : 1;
        var results = Path.Combine(root, "iterations.csv");
        var helperStarts = 0;
        var paths = AppPaths.CreateAndVerify(root);
        var db = new Database(paths.DatabasePath); db.Initialize();
        var secret = new ExportPasswordStore(Path.Combine(paths.DataDirectory, "local", "export-password.dpapi"));
        if (!second)
        {
            var site = new Site { Name = "restart baseline", Url = "https://example.test/", MonitorMode = MonitorMode.Text, ScheduleMode = ScheduleMode.Manual };
            db.SaveSite(site); db.ApplySuccess(site.Id, "A", "A", null, null, MonitorMode.Text, DateTimeOffset.Now);
            db.ApplySuccess(site.Id, "B", "B", null, null, MonitorMode.Text, DateTimeOffset.Now);
            secret.Save("probe-only-password");
            using var current = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(root, "old-process.txt"), current.Id + ":" + current.StartTime.ToUniversalTime().Ticks);
        }
        using var single = new SingleInstanceCoordinator(Path.GetFileName(root));
        if (!single.IsPrimary)
        {
            single.NotifyPrimaryAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "SECONDARY"), Environment.ProcessId.ToString()); return;
        }
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var fetcher = new SharedHttpFetcher(client);
        using var context = new TrayApplicationContext(paths, single, false, true, fetcher, () => { helperStarts++; return RestartLauncher.StartHelper(argument); }, () => true);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        var stage = 0;
        var validated = false;
        timer.Tick += (_, _) =>
        {
            if (second && !validated)
            {
                validated = true;
                var main = Application.OpenForms.OfType<MainForm>().Single();
                var identity = File.ReadAllText(Path.Combine(root, "old-process.txt")).Split(':');
                var oldAlive = false;
                try { using var old = Process.GetProcessById(int.Parse(identity[0])); oldAlive = old.StartTime.ToUniversalTime().Ticks == long.Parse(identity[1]); } catch (ArgumentException) { }
                var tray = Get<NotifyIcon>(context, "_tray");
                var ok = !oldAlive && main.Visible && main.Enabled && tray.Visible && tray.ContextMenuStrip!.Enabled
                    && db.GetSchemaVersion() == 7 && db.GetSites().Single().LastHash == "B"
                    && db.GetHistory().Count == 1 && db.GetNextPendingUpdateDialog() is not null && secret.TryLoad() == "probe-only-password"
                    && main.Text == ProductInfo.Title && Get<AppSettings>(context, "_settings").UiFontSize == 11 && main.Width == 1234;
                var completed = File.Exists(results) ? File.ReadAllLines(results).Length : 0;
                File.AppendAllText(results, $"{completed + 1},{identity[0]},{Environment.ProcessId},{ok},{!oldAlive},{main.Visible && main.Enabled},{tray.Visible && tray.ContextMenuStrip!.Enabled},schema7-history1-FIFO-DPAPI-font11-width1234{Environment.NewLine}");
                if (!ok || completed + 1 == repeats)
                {
                    timer.Stop();
                    File.WriteAllText(Path.Combine(root, ok ? "SUCCESS" : "FAILED"), Environment.ProcessId.ToString());
                    tray.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(item => item.Text == "終了").PerformClick();
                }
            }
            else if (stage == 0)
            {
                stage = 1;
                _ = Get<OneShotScheduler>(context, "_scheduler").CheckNowAsync(db.GetSites());
            }
            else if (handler.Started)
            {
                timer.Stop();
                var main = Application.OpenForms.OfType<MainForm>().Single();
                main.Width = 1234;
                if (!second)
                {
                    var zoom = Message.Create(main.Handle, 0x0100, (IntPtr)Keys.Oemplus, IntPtr.Zero);
                    Get<FontZoomMessageFilter>(context, "_fontZoom").HandleMessage(ref zoom, Keys.Control | Keys.Shift);
                }
                // Exercise X-to-tray and the real tray callback without acknowledging FIFO entries.
                main.Close();
                var resident = Get<NotifyIcon>(context, "_tray");
                if (Application.OpenForms.OfType<MainForm>().Any() || !resident.Visible) { File.WriteAllText(Path.Combine(root, "FAILED"), "X-to-tray"); return; }
                resident.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(item => item.Text == "WebSite Monitorを開く").PerformClick();
                main = Application.OpenForms.OfType<MainForm>().Single();
                if (!main.Visible) { File.WriteAllText(Path.Combine(root, "FAILED"), "tray return"); return; }
                using (var duplicate = new SingleInstanceCoordinator(Path.GetFileName(root)))
                {
                    if (duplicate.IsPrimary) { File.WriteAllText(Path.Combine(root, "FAILED"), "duplicate mutex"); return; }
                }
                using var current = Process.GetCurrentProcess();
                File.WriteAllText(Path.Combine(root, "old-process.txt"), current.Id + ":" + current.StartTime.ToUniversalTime().Ticks);
                var tray = File.Exists(Path.Combine(root, "tray"));
                if (tray) Get<NotifyIcon>(context, "_tray").ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(item => item.Text == "再起動").PerformClick();
                else main.Controls.OfType<ToolStrip>().SelectMany(strip => strip.Items.Cast<ToolStripItem>()).Single(item => item.Text == (closeOnly ? "完全に閉じる" : "再起動")).PerformClick();
            }
        };
        timer.Start();
        Application.Run(context);
        if (handler.Active != 0 || helperStarts > 1) File.WriteAllText(Path.Combine(root, "FAILED"), "orphan fetch / duplicate helper");
        if (closeOnly)
        {
            var ok = handler.Active == 0 && helperStarts == 0 && !Get<NotifyIcon>(context, "_tray").Visible
                && new SettingsStore(paths.SettingsPath).Load().WindowWidth == 1234 && new SettingsStore(paths.SettingsPath).Load().UiFontSize == 11 && db.GetSites().Single().LastHash == "B"
                && db.GetHistory().Count == 1 && db.GetNextPendingUpdateDialog() is not null && secret.TryLoad() == "probe-only-password";
            File.WriteAllText(Path.Combine(root, ok ? "SUCCESS" : "FAILED"), "closed-without-helper");
        }
    }
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal volatile bool Started;
        internal int Active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Active); Started = true;
            try { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
