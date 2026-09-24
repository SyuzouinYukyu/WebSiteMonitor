using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--ui-probe", StringComparer.OrdinalIgnoreCase))
            {
                UiProbeApplication.Run(args.Contains("--ui-probe-auto-close", StringComparer.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(12) : null, args.Contains("--ui-probe-100-sites", StringComparer.OrdinalIgnoreCase) ? 100 : 1, ReadUiProbeFontSize(args), ReadUiProbeWidth(args));
                return;
            }
            var paths = AppPaths.CreateAndVerify();
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                var database = new Database(paths.DatabasePath); database.Initialize();
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
            MessageBox.Show("WebSiteMonitorを起動できません。\n\n" + ex.Message, "WebSiteMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        catch (Exception ex) { throw new IOException("アプリのフォルダーへ書き込めません。書き込み可能な場所へWebSiteMonitorを移動してください。", ex); }
        return result;
    }
}