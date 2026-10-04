using Microsoft.Win32;

namespace WebSiteMonitor.Tests;

public sealed class WindowsIntegrationTests
{
    [Fact]
    public void AutoStartRegistryCanBeEnabledAndDisabled()
    {
        var keyPath = @"Software\WebSiteMonitor.Tests\" + Guid.NewGuid().ToString("N");
        using var actualRun = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var original = actualRun?.GetValue("WebSiteMonitor");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true)!;
            const string executable = @"C:\Portable Test\WebSiteMonitor_v1.1.3.exe";
            global::WebSiteMonitor.WindowsIntegration.SetAutoStart(true, executable, key);
            Assert.Equal($"\"{executable}\" --autostart", key.GetValue("WebSiteMonitor") as string);
            global::WebSiteMonitor.WindowsIntegration.SetAutoStart(false, executable, key);
            Assert.Null(key.GetValue("WebSiteMonitor"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }
        Assert.Equal(original, actualRun?.GetValue("WebSiteMonitor"));
    }
}
