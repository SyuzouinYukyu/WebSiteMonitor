using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WebSiteMonitor.Core;

namespace WebSiteMonitor.Tests;

public sealed class ConfigurationTransferTests : IDisposable
{
    private const string Password = "Test-only-password-123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WebSiteMonitorConfigTests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "test.db");
    private string SettingsPath => Path.Combine(_root, "settings.json");
    public ConfigurationTransferTests() => Directory.CreateDirectory(_root);
    private Database Db() { var db = new Database(DbPath); db.Initialize(); return db; }
    private static Site Site(string name = "日本語の監視サイト") => new()
    {
        Name = name, Url = "https://example.test/page?authsystem_feed_token=TEST_SECRET_TOKEN", NotificationTargetUrl = "https://example.test/open",
        MonitorMode = MonitorMode.Regex, Regex = "value.*", Selector = "div", XPath = "//div", ScheduleMode = ScheduleMode.Daily, DailyTime = "23:17", IntervalMinutes = 75,
        UseBrowserCompatibleUserAgent = true, Enabled = false, WindowsNotification = false, PopupNotification = true,
        UpdateDialogNotification = false, SoundNotification = true, SoundVolume = 37, SoundFile = @"Z:\missing\日本語音源.mp3"
    };
    private static AppSettings Settings() => new()
    {
        UiFontSize = 18, WindowX = 25, WindowY = 36, WindowWidth = 1400, WindowHeight = 820,
        ColumnWidths = new() { ["Name"] = 320, ["Url"] = 450 }, StartWithWindows = true, WindowsIntegrationEnabled = true,
        NotificationsEnabled = false, DefaultPopup = true, HistoryRetentionDays = 120, LogRetentionDays = 25,
        PcmSampleRate = 48000, PcmBits = 24, PcmChannels = 1
    };
    private void Sql(string sql) { using var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private string Snapshot(Database db) => JsonSerializer.Serialize(new { Sites = db.GetSites(), History = db.GetHistory(), Pending = db.GetNextPendingUpdateDialog(), Settings = File.Exists(SettingsPath) ? Convert.ToBase64String(File.ReadAllBytes(SettingsPath)) : null });

    [Fact]
    public void EncryptedRoundTripRestoresAllEditableFieldsWithoutExportingSecretsOrInternalState()
    {
        var source = Site(); source.LastHash = "INTERNAL_HASH"; source.AutoDetectedFeedUrl = "https://internal.test/"; source.Id = 123;
        var feed = new Site { Name = "RSS資格情報", Url = "https://example.test/", MonitorMode = MonitorMode.Feed, FeedUrl = "https://example.test/feed?password=TEST_SECRET_FEED", SoundVolume = 0, UpdateDialogNotification = true };
        var document = ConfigurationTransfer.Capture([source, feed], Settings());
        var file = Path.Combine(_root, "設定.wsmcfg");
        ConfigurationTransfer.Export(file, document, Password);
        var encrypted = File.ReadAllBytes(file);
        var text = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("TEST_SECRET_TOKEN", text); Assert.DoesNotContain(Password, text); Assert.DoesNotContain("INTERNAL_HASH", text);
        Assert.DoesNotContain("TEST_SECRET_FEED", text);
        Assert.Equal(200000, BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(12, 4)));
        var restored = ConfigurationTransfer.Read(file, Password);
        Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(restored));
        var json = JsonSerializer.Serialize(restored);
        foreach (var field in new[] { "\"Id\"", "MonitorRevision", "AutoDetectedFeedUrl", "LastHash", "LastChecked", "LastError", "NextDue" }) Assert.DoesNotContain(field, json);
        var db = Db(); var store = new SettingsStore(SettingsPath); store.Save(new AppSettings());
        var result = ConfigurationTransfer.Import(restored, ConfigurationImportMode.Merge, db, store);
        Assert.Equal(2, result.ImportedSites); Assert.Equal(1, result.MissingSoundFiles);
        Assert.Equal(JsonSerializer.Serialize(document.Sites.OrderBy(s => s.Name, StringComparer.Ordinal)), JsonSerializer.Serialize(db.GetSites().Select(SiteSettings.FromSite).OrderBy(s => s.Name, StringComparer.Ordinal)));
        Assert.Equal(JsonSerializer.Serialize(Settings()), JsonSerializer.Serialize(store.Load()));
        Assert.Equal(7, db.GetSchemaVersion()); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void BadPasswordTamperingVersionsAndMissingRequiredFieldsRejectWithoutChangingExistingData()
    {
        var db = Db(); db.SaveSite(Site()); new SettingsStore(SettingsPath).Save(Settings());
        var snapshot = Snapshot(db);
        var document = ConfigurationTransfer.Capture(db.GetSites(), Settings());
        var bytes = ConfigurationTransfer.Encrypt(document, Password);
        Assert.Equal(ConfigurationTransfer.ReadError, Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(bytes, "wrong-password")).Message);
        var changed = bytes.ToArray(); changed[^1] ^= 1;
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(changed, Password));
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(bytes[..30], Password));
        changed = bytes.ToArray(); changed[8] = 2;
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(changed, Password));
        var json = JsonNode.Parse(JsonSerializer.Serialize(document))!.AsObject();
        json["FormatVersion"] = 99;
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(json), Password));
        json["FormatVersion"] = 1;
        json["AppSettings"]!.AsObject().Remove("NotificationsEnabled");
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(json), Password));
        json = JsonNode.Parse(JsonSerializer.Serialize(document))!.AsObject();
        json["Sites"]![0]!.AsObject().Remove("UpdateDialogNotification");
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(json), Password));
        json = JsonNode.Parse(JsonSerializer.Serialize(document))!.AsObject(); json["Sites"]![0]!["SoundVolume"] = 101;
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Decrypt(EncryptRaw(json), Password));
        Assert.Equal(snapshot, Snapshot(db));
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Encrypt(document, "short"));
    }

    [Fact]
    public void MergeIsIdempotentDistinguishesMonitoringDefinitionsAndPreservesExistingHistoryFifoAndState()
    {
        var db = Db(); var site = Site(); site.Enabled = true; site.UpdateDialogNotification = true; db.SaveSite(site);
        db.ApplySuccess(site.Id, "A", "old", null, null, MonitorMode.Regex, DateTimeOffset.Now);
        db.ApplySuccess(site.Id, "B", "new", null, null, MonitorMode.Regex, DateTimeOffset.Now);
        var before = db.GetSite(site.Id)!; var history = db.GetHistory(); var pending = db.GetNextPendingUpdateDialog();
        var incoming = Site("取り込み後"); incoming.SoundVolume = 0;
        var other = Site("別抽出"); other.Regex = "different.*";
        var document = ConfigurationTransfer.Capture([incoming, other], Settings());
        var store = new SettingsStore(SettingsPath); store.Save(new AppSettings());
        ConfigurationTransfer.Import(document, ConfigurationImportMode.Merge, db, store);
        ConfigurationTransfer.Import(document, ConfigurationImportMode.Merge, db, store);
        Assert.Equal(2, db.GetSites().Count);
        var after = db.GetSite(site.Id)!;
        Assert.Equal("取り込み後", after.Name); Assert.Equal(0, after.SoundVolume); Assert.False(after.UpdateDialogNotification);
        Assert.Equal(before.LastHash, after.LastHash); Assert.Equal(before.LastChecked, after.LastChecked); Assert.Equal(before.MonitorRevision, after.MonitorRevision);
        // SiteName is a live join against the editable site name, not a stored history field.
        Assert.Equal(history.Select(h => h with { SiteName = "" }), db.GetHistory().Select(h => h with { SiteName = "" })); Assert.Equal(pending, db.GetNextPendingUpdateDialog());
    }

    [Fact]
    public void ReplaceRetainsReferencedDefinitionsAndSafelyRejectsRemovalOfHistoryOrPendingNotifications()
    {
        var db = Db(); var site = Site(); site.UpdateDialogNotification = true; db.SaveSite(site);
        db.ApplySuccess(site.Id, "A", "old", null, null, MonitorMode.Regex, DateTimeOffset.Now);
        db.ApplySuccess(site.Id, "B", "new", null, null, MonitorMode.Regex, DateTimeOffset.Now);
        var spare = new Site { Name = "履歴なし", Url = "https://spare.test/" }; db.SaveSite(spare);
        var history = db.GetHistory(); var pending = db.GetNextPendingUpdateDialog();
        var store = new SettingsStore(SettingsPath); store.Save(new AppSettings());
        var kept = Site("保持する設定"); kept.SoundVolume = 100; kept.UpdateDialogNotification = true;
        ConfigurationTransfer.Import(ConfigurationTransfer.Capture([kept], Settings()), ConfigurationImportMode.Replace, db, store);
        Assert.Single(db.GetSites()); Assert.Equal(site.Id, db.GetSites()[0].Id);
        Assert.Equal(history.Select(h => h with { SiteName = "" }), db.GetHistory().Select(h => h with { SiteName = "" })); Assert.Equal(pending, db.GetNextPendingUpdateDialog());
        var snapshot = Snapshot(db);
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Import(ConfigurationTransfer.Capture([], new AppSettings()), ConfigurationImportMode.Replace, db, store));
        Assert.Equal(snapshot, Snapshot(db)); Assert.Equal(7, db.GetSchemaVersion());
    }

    [Fact]
    public void DatabaseFailureBeforeAndAtCommitRollsBackSitesAndExactSettingsBytes()
    {
        var db = Db(); var site = Site(); db.SaveSite(site);
        var store = new SettingsStore(SettingsPath); store.Save(new AppSettings());
        var incoming = Site("変化する名前"); var document = ConfigurationTransfer.Capture([incoming], Settings());
        Sql("CREATE TRIGGER FailWrite BEFORE UPDATE ON Sites BEGIN SELECT RAISE(ABORT,'TEST_SECRET_TOKEN'); END;");
        var snapshot = Snapshot(db);
        var failure = Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Import(document, ConfigurationImportMode.Merge, db, store));
        Assert.DoesNotContain("TEST_SECRET_TOKEN", failure.Message); Assert.Equal(snapshot, Snapshot(db));
        Sql("DROP TRIGGER FailWrite; CREATE TABLE TestDeferredFailure(SiteId INTEGER REFERENCES Sites(Id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER FailCommit AFTER UPDATE ON Sites BEGIN INSERT INTO TestDeferredFailure VALUES(-999); END;");
        Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Import(document, ConfigurationImportMode.Merge, db, store));
        Assert.Equal(snapshot, Snapshot(db)); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SettingsWriteFailureRollsBackDatabaseAndKeepsOriginalFile()
    {
        var db = Db(); db.SaveSite(Site()); var store = new SettingsStore(SettingsPath); store.Save(new AppSettings());
        var snapshot = Snapshot(db); var document = ConfigurationTransfer.Capture([Site("変更後")], Settings());
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ConfigurationException>(() => ConfigurationTransfer.Import(document, ConfigurationImportMode.Merge, db, store));
        Assert.Equal(snapshot, Snapshot(db)); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static byte[] EncryptRaw(JsonNode document)
    {
        var plain = Encoding.UTF8.GetBytes(document.ToJsonString());
        var bytes = new byte[64 + plain.Length]; Encoding.ASCII.GetBytes("WSMCFG01").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), 200000);
        RandomNumberGenerator.Fill(bytes.AsSpan(16, 16)); RandomNumberGenerator.Fill(bytes.AsSpan(32, 12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44, 4), plain.Length);
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, bytes.AsSpan(16, 16), 200000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16); aes.Encrypt(bytes.AsSpan(32, 12), plain, bytes.AsSpan(64), bytes.AsSpan(48, 16), bytes.AsSpan(0, 48));
        CryptographicOperations.ZeroMemory(key); return bytes;
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
}
