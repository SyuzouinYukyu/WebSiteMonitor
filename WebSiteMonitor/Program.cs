using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // No paths, Mutex or UI are initialized while the previous process is alive.
        if (args.Any(argument => argument.StartsWith(RestartLauncher.ArgumentPrefix, StringComparison.Ordinal)))
        {
            var result = RestartLauncher.CompleteHelper(args);
            if (!RestartLauncher.IsSuccess(result))
            {
                var probe = args.FirstOrDefault(argument => argument.StartsWith(RestartProbeApplication.ArgumentPrefix, StringComparison.Ordinal));
                if (probe is not null) RestartProbeApplication.ReportHelperFailure(probe, result);
                else MessageBox.Show("旧プロセスの安全な終了を確認できないため再起動を中止しました。原因区分: " + result + "。通常起動は自動実行しません。", "再起動", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        ApplicationConfiguration.Initialize();
        try
        {
            var restartProbe = args.FirstOrDefault(argument => argument.StartsWith(RestartProbeApplication.ArgumentPrefix, StringComparison.Ordinal));
            if (restartProbe is not null) { RestartProbeApplication.Run(restartProbe); return; }
            if (args.Contains("--ui-probe", StringComparer.OrdinalIgnoreCase))
            {
                UiProbeApplication.Run(args.Contains("--ui-probe-auto-close", StringComparer.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(12) : null, args.Contains("--ui-probe-100-sites", StringComparer.OrdinalIgnoreCase) ? 100 : 1, ReadUiProbeFontSize(args), ReadUiProbeWidth(args));
                return;
            }
            var paths = AppPaths.CreateAndVerify();
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                var database = new Database(paths.DatabasePath); database.Initialize();
                var resources = typeof(Program).Assembly.GetManifestResourceNames();
                if (resources.Any(name => name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("不要なFFmpegリソースが存在します。");
                if (!resources.Contains("WebSiteMonitor.Resources.THIRD_PARTY_NOTICES.md")) throw new InvalidOperationException("ライセンスリソースが見つかりません。");
                File.WriteAllLines(Path.Combine(paths.DataDirectory, "SELF_TEST_RESOURCES.txt"), resources);
                File.WriteAllText(Path.Combine(paths.DataDirectory, "SELF_TEST_OK"), "SELF_TEST_OK");
                return;
            }
            using var single = new SingleInstanceCoordinator();
            if (!single.IsPrimary)
            {
                single.NotifyPrimaryAsync().GetAwaiter().GetResult();
                return;
            }
            using var context = new TrayApplicationContext(paths, single, args.Contains("--autostart", StringComparer.OrdinalIgnoreCase));
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebSite Monitorを起動できません。\n\n" + DisplayText.Exception(ex), "WebSite Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
private static int? ReadUiProbeWidth(IEnumerable<string> args)
    {
        const string prefix = "--ui-probe-width=";
        var value = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is not null && int.TryParse(value[prefix.Length..], out var parsed) ? Math.Clamp(parsed, 980, 3840) : null;
    }

private static double? ReadUiProbeFontSize(IEnumerable<string> args)
    {
        const string prefix = "--ui-probe-font-size=";
        var value = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is not null && double.TryParse(value[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? UiFontSettings.Clamp(parsed)
            : null;
    }
}

internal sealed record AppPaths(string BaseDirectory, string DataDirectory, string SettingsPath, string DatabasePath, string LogsDirectory, string SoundsDirectory, string CacheDirectory)
{
    public static AppPaths CreateAndVerify(string? baseDirectory = null)
    {
        var baseDir = (baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var data = Path.Combine(baseDir, "data");
        var result = new AppPaths(baseDir, data, Path.Combine(data, "settings.json"), Path.Combine(data, "WebSiteMonitor.db"), Path.Combine(data, "logs"), Path.Combine(data, "sounds"), Path.Combine(data, "cache"));
        try
        {
            Directory.CreateDirectory(result.DataDirectory); Directory.CreateDirectory(result.LogsDirectory); Directory.CreateDirectory(result.SoundsDirectory); Directory.CreateDirectory(result.CacheDirectory);
            var probe = Path.Combine(result.CacheDirectory, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "ok"); File.Delete(probe);
        }
        catch (Exception ex) { throw new IOException("アプリのフォルダーへ書き込めません。書き込み可能な場所へWebSite Monitorを移動してください。", ex); }
        return result;
    }
}
