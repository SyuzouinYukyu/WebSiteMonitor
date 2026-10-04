using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebSiteMonitor.Core;

public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}

// Only editable settings belong here. Site IDs, revision, history and fetch state never travel.
public sealed class SiteSettings
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string? NotificationTargetUrl { get; set; }
    public required bool UseBrowserCompatibleUserAgent { get; set; }
    public required bool Enabled { get; set; }
    public required MonitorMode MonitorMode { get; set; }
    public required string? FeedUrl { get; set; }
    public required string? Selector { get; set; }
    public required string? XPath { get; set; }
    public required string? Regex { get; set; }
    public required ScheduleMode ScheduleMode { get; set; }
    public required int IntervalMinutes { get; set; }
    public required string DailyTime { get; set; }
    public required bool WindowsNotification { get; set; }
    public required bool PopupNotification { get; set; }
    public required bool UpdateDialogNotification { get; set; }
    public required bool SoundNotification { get; set; }
    public required string? SoundFile { get; set; }
    public required int SoundVolume { get; set; }

    private static readonly PropertyInfo[] Fields = typeof(SiteSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);
    public static SiteSettings FromSite(Site site)
    {
        // JSON required-member checking applies on input; this factory fills every editable field.
        var dto = JsonSerializer.Deserialize<SiteSettings>(JsonSerializer.Serialize(Fields.ToDictionary(p => p.Name,
            p => typeof(Site).GetProperty(p.Name)!.GetValue(site))))!;
        return dto;
    }
    public Site ApplyTo(Site? site = null)
    {
        site ??= new Site();
        foreach (var p in Fields) typeof(Site).GetProperty(p.Name)!.SetValue(site, p.GetValue(this));
        return site;
    }
    // Match the existing monitoring-revision definition, including User-Agent. No URL normalization
    // removes credentials or equates two differently configured monitoring targets.
    public bool Matches(Site site) => !SiteValidation.MonitoringConfigurationChanged(site, ApplyTo());
}

public sealed class ConfigurationDocument
{
    public required int FormatVersion { get; set; }
    public required List<SiteSettings> Sites { get; set; }
    public required AppSettings AppSettings { get; set; }
}

public enum ConfigurationImportMode { Merge, Replace }
public sealed record ConfigurationImportResult(int ImportedSites, int MissingSoundFiles);

public static class ConfigurationTransfer
{
    public const int FormatVersion = 1;
    public const int Iterations = 200_000;
    public const int MinimumPasswordLength = 8;
    public const string ReadError = "設定ファイルを読み込めません。パスワード、ファイルの破損、対応形式を確認してください。既存設定は変更していません。";
    private const int HeaderLength = 64;
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WSMCFG01");
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ConfigurationDocument Capture(IEnumerable<Site> sites, AppSettings settings) => new()
    {
        FormatVersion = FormatVersion,
        Sites = sites.Select(SiteSettings.FromSite).ToList(),
        AppSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!
    };

    public static byte[] Encrypt(ConfigurationDocument document, string password)
    {
        Validate(document);
        if (password.Length < MinimumPasswordLength) throw new ConfigurationException("パスワードは8文字以上で指定してください。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (plain.Length > MaxBytes - HeaderLength) { CryptographicOperations.ZeroMemory(plain); throw new ConfigurationException("設定データが大きすぎます。"); }
        var output = new byte[HeaderLength + plain.Length];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), Iterations);
        RandomNumberGenerator.Fill(output.AsSpan(16, 16));
        RandomNumberGenerator.Fill(output.AsSpan(32, 12));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(44, 4), plain.Length);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(16, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(32, 12), plain, output.AsSpan(HeaderLength), output.AsSpan(48, 16), output.AsSpan(0, 48));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    public static ConfigurationDocument Decrypt(byte[] input, string password)
    {
        byte[]? plain = null;
        byte[]? key = null;
        try
        {
            if (password.Length < MinimumPasswordLength || input.Length <= HeaderLength || input.Length > MaxBytes
                || !input.AsSpan(0, 8).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(8, 4)) != FormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(44, 4)) != input.Length - HeaderLength) throw new InvalidDataException();
            var iterations = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(12, 4));
            if (iterations is < Iterations or > 2_000_000) throw new InvalidDataException();
            key = Rfc2898DeriveBytes.Pbkdf2(password, input.AsSpan(16, 16), iterations, HashAlgorithmName.SHA256, 32);
            plain = new byte[input.Length - HeaderLength];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(input.AsSpan(32, 12), input.AsSpan(HeaderLength), input.AsSpan(48, 16), plain, input.AsSpan(0, 48));
            using var json = JsonDocument.Parse(plain);
            RequireFields(json.RootElement, typeof(ConfigurationDocument));
            RequireFields(json.RootElement.GetProperty("AppSettings"), typeof(AppSettings));
            foreach (var site in json.RootElement.GetProperty("Sites").EnumerateArray()) RequireFields(site, typeof(SiteSettings));
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(plain, JsonOptions) ?? throw new InvalidDataException();
            Validate(document);
            return document;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException or ConfigurationException)
        { throw new ConfigurationException(ReadError); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void RequireFields(JsonElement element, Type type)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        var expected = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || expected.Any(p => !names.Contains(p, StringComparer.Ordinal))) throw new InvalidDataException();
    }

    public static void Validate(ConfigurationDocument document)
    {
        try
        {
            if (document.FormatVersion != FormatVersion || document.Sites is null || document.Sites.Count > 10000 || document.AppSettings is null) throw new InvalidDataException();
            foreach (var dto in document.Sites)
            {
                if (dto is null) throw new InvalidDataException();
                var site = dto.ApplyTo();
                if (!Enum.IsDefined(site.MonitorMode) || !Enum.IsDefined(site.ScheduleMode) || site.FeedUrl is not null && site.MonitorMode != MonitorMode.Feed) throw new InvalidDataException();
                if (typeof(SiteSettings).GetProperties().Any(p => p.GetValue(dto) is string s && s.Length > 32768)) throw new InvalidDataException();
                SiteValidation.Validate(site);
            }
            var a = document.AppSettings;
            if (!double.IsFinite(a.UiFontSize) || a.UiFontSize is < 10 or > 18
                || a.WindowWidth is < 1 or > 32768 || a.WindowHeight is < 1 or > 32768
                || a.ColumnWidths is null || a.ColumnWidths.Count > 100 || a.ColumnWidths.Any(p => p.Key.Length > 100 || p.Value is < 1 or > 32768)
                || a.HistoryRetentionDays is < 1 or > 3650 || a.LogRetentionDays is < 1 or > 3650
                || a.PcmSampleRate is < 8000 or > 192000 || a.PcmBits is not (8 or 16 or 24 or 32) || a.PcmChannels is < 1 or > 8
                || a.StartWithWindows && !a.WindowsIntegrationEnabled) throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        { throw new ConfigurationException("設定ファイルの形式または設定値が不正です。既存設定は変更していません。"); }
    }

    public static void Export(string path, ConfigurationDocument document, string password)
    {
        var encrypted = Encrypt(document, password);
        try { SettingsStore.WriteAtomic(path, encrypted); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new ConfigurationException("設定ファイルを保存できません。保存先を確認してください。"); }
    }

    public static ConfigurationDocument Read(string path, string password)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new InvalidDataException();
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return Decrypt(bytes, password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { throw new ConfigurationException(ReadError); }
    }

    public static ConfigurationImportResult Import(ConfigurationDocument document, ConfigurationImportMode mode, Database database, SettingsStore store)
    {
        Validate(document);
        if (!Enum.IsDefined(mode)) throw new ConfigurationException("取り込み方式が不正です。");
        var missing = document.Sites.Count(s => !string.IsNullOrWhiteSpace(s.SoundFile) && Path.IsPathFullyQualified(s.SoundFile) && !File.Exists(s.SoundFile));
        try { store.SaveCoordinated(document.AppSettings, save => database.ImportUserSettings(document.Sites, mode, save)); }
        catch (ConfigurationException) { throw; }
        catch { throw new ConfigurationException("設定の取り込みに失敗しました。既存設定を復元しました。保存先の状態を確認してください。"); }
        return new(document.Sites.Count, missing);
    }
}
