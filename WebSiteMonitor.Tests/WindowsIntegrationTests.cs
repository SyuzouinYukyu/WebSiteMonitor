using Microsoft.Win32;

namespace WebSiteMonitor.Tests;

public sealed class WindowsIntegrationTests
{
    [Fact]
    public void AutoStartRegistryCanBeEnabledAndDisabled()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, true)!;
        var original = key.GetValue("WebSiteMonitor") as string;
        try
        {
            const string executable = @"C:\Portable Test\WebSiteMonitor.exe";
            global::WebSiteMonitor.WindowsIntegration.SetAutoStart(true, executable);
            Assert.Equal($"\"{executable}\" --autostart", key.GetValue("WebSiteMonitor") as string);
            global::WebSiteMonitor.WindowsIntegration.SetAutoStart(false, executable);
            Assert.Null(key.GetValue("WebSiteMonitor"));
        }
        finally
        {
            if (original is null) key.DeleteValue("WebSiteMonitor", false);
            else key.SetValue("WebSiteMonitor", original, RegistryValueKind.String);
        }
    }
}
