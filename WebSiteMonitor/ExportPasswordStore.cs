using System.Security.Cryptography;
using System.Text;
using WebSiteMonitor.Core;

namespace WebSiteMonitor;

// Local-only state, deliberately separate from AppSettings and ConfigurationTransfer.
internal sealed class ExportPasswordStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WebSiteMonitor.ExportPassword.v1");
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly Action<string, byte[]> _write;

    internal ExportPasswordStore(string path, Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null, Action<string, byte[]>? write = null)
    {
        _path = path;
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        _write = write ?? SettingsStore.WriteAtomic;
    }

    internal string? TryLoad()
    {
        byte[]? plain = null;
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length is < 1 or > 65536) return null;
            plain = _unprotect(File.ReadAllBytes(_path));
            var password = new UTF8Encoding(false, true).GetString(plain);
            return IsValid(password) ? password : null;
        }
        catch { return null; } // Damaged / foreign-user blobs only cause a fresh prompt.
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    internal static bool IsValid(string password) => password.Length is >= ConfigurationTransfer.MinimumPasswordLength and <= 1024
        && !string.IsNullOrWhiteSpace(password);

    internal void Save(string password)
    {
        if (!IsValid(password)) throw new ArgumentException("パスワードは8～1024文字で指定してください。");
        var plain = Encoding.UTF8.GetBytes(password);
        try { _write(_path, _protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Forget() => File.Delete(_path);
    internal string? ResolvePassword(bool importing, Func<string?> prompt) => importing ? prompt() : TryLoad() ?? prompt();

    internal bool ExportAndRemember(string path, ConfigurationDocument document, string password)
    {
        ConfigurationTransfer.Export(path, document, password);
        try { Save(password); return true; }
        catch { return false; } // The successful encrypted backup is never rolled back.
    }
}
