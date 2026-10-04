using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class FontSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorFontSettings-" + Guid.NewGuid().ToString("N"));

    public FontSettingsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void DefaultAndInvalidFontSizesAreNormalizedToTheSupportedRange()
    {
        Assert.Equal(10.0, new AppSettings().UiFontSize);
        Assert.Equal(10.0, global::WebSiteMonitor.UiFontSettings.Clamp(double.NaN));
        Assert.Equal(10.0, global::WebSiteMonitor.UiFontSettings.Clamp(double.NegativeInfinity));
        Assert.Equal(10.0, global::WebSiteMonitor.UiFontSettings.Clamp(-100));
        Assert.Equal(18.0, global::WebSiteMonitor.UiFontSettings.Clamp(100));
    }

    [Fact]
    public void FontSizeIsPersistedAndLegacyOutOfRangeValuesAreClampedWithoutLosingOtherSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { UiFontSize = 14.0, HistoryRetentionDays = 321, NotificationsEnabled = false });
        var saved = store.Load();
        Assert.Equal(14.0, saved.UiFontSize);
        Assert.Equal(321, saved.HistoryRetentionDays);
        Assert.False(saved.NotificationsEnabled);

        File.WriteAllText(path, "{\"UiFontSize\":-1,\"HistoryRetentionDays\":321,\"NotificationsEnabled\":false}");
        var minimum = new SettingsStore(path).Load();
        Assert.Equal(10.0, minimum.UiFontSize);
        Assert.Equal(321, minimum.HistoryRetentionDays);
        Assert.False(minimum.NotificationsEnabled);

        File.WriteAllText(path, "{\"UiFontSize\":99,\"HistoryRetentionDays\":321,\"NotificationsEnabled\":false}");
        var maximum = new SettingsStore(path).Load();
        Assert.Equal(18.0, maximum.UiFontSize);
        Assert.Equal(321, maximum.HistoryRetentionDays);
        Assert.False(maximum.NotificationsEnabled);
    }
    [Fact]
    public void MissingLegacyFontSizeIsDefaultedAndWrittenOnTheNextSave()
    {
        var path = Path.Combine(_root, "legacy-settings.json");
        File.WriteAllText(path, "{\"WindowWidth\":1200,\"HistoryRetentionDays\":222}");
        var store = new SettingsStore(path);
        var legacy = store.Load();
        Assert.Equal(10.0, legacy.UiFontSize);
        Assert.Equal(1200, legacy.WindowWidth);
        Assert.Equal(222, legacy.HistoryRetentionDays);
        store.Save(legacy);
        Assert.Contains("\"UiFontSize\"", File.ReadAllText(path));
    }
}
