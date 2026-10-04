using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebSiteMonitor.Core;

public static class NotificationUrl
{
    private const int MaximumUrlLength = 8192;
    // HTML text extraction can join adjacent nodes. Credentials must remain redacted
    // even when a preceding node removes the usual word boundary.
    private static readonly Regex UrlInText = new(@"(?:[a-z][a-z0-9+.-]*://|https?%(?:25){0,2}3a%(?:25){0,2}2f%(?:25){0,2}2f)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BearerInText = new(@"Bearer\s+[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "auth", "authsystemfeedtoken", "accesstoken", "apikey", "secret", "password",
        "session", "sessionid", "sid", "signature", "sig", "clientsecret", "credential",
        "authorization", "bearer", "jwt", "privatekey", "passcode", "key", "passwd", "pwd",
        "oauthcode", "code", "nonce", "state"
    };

    public static string ValidateExplicit(string url)
    {
        if (!TrySafePage(url, out var safe))
            throw new ArgumentException("通知先URLには、認証情報を含まない有効なhttp/https公開ページURLを指定してください。");
        return safe;
    }

    // Apply the same rule to new snapshots and old v1.0.5 pending rows.
    public static string Sanitize(string? url)
    {
        if (TrySafePage(url, out var safe)) return safe;
        return TryOrigin(url, out var root) ? root : "";
    }

    public static string ForSite(Site site, string? feedXml = null)
    {
        if (!string.IsNullOrWhiteSpace(site.NotificationTargetUrl))
            return Sanitize(site.NotificationTargetUrl);
        // The persisted baseline is a whole-feed hash/preview, not per-entry state.
        // It cannot identify a changed entry reliably, so never guess from feed order.
        return Sanitize(site.Url);
    }

    public static string ForEvent(CheckResult result) => Sanitize(result.NotificationTargetUrl ?? ForSite(result.Site));

    public static string ForStored(string? url, string? configuredDestination = null)
    {
        if (TrySafePage(url, out var safe)) return safe;
        if (TrySafePage(configuredDestination, out safe)) return safe;
        return Sanitize(url);
    }

    public static string RedactText(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        try
        {
            if (message.Length > 65536) return "[DIAGNOSTIC_REDACTED]";
            var text = RedactParameters(message);
            text = UrlInText.Replace(text, "[URL_REDACTED]");
            return BearerInText.Replace(text, "[CREDENTIAL_REDACTED]");
        }
        catch { return "[DIAGNOSTIC_REDACTED]"; }
    }

    // External exception text is untrusted even when no known secret key is present.
    // Keep only fixed classifications, structural labels and redaction markers; never its free-form body.
    public static string SafeException(Exception exception)
    {
        try
        {
            var category = exception switch
            {
                HttpRequestException => "HttpRequestException",
                OperationCanceledException => "OperationCanceledException",
                TimeoutException => "TimeoutException",
                IOException => "IOException",
                UnauthorizedAccessException => "UnauthorizedAccessException",
                InvalidOperationException => "InvalidOperationException",
                ArgumentException => "ArgumentException",
                FormatException => "FormatException",
                JsonException => "JsonException",
                System.Xml.XmlException => "XmlException",
                _ => "System.Exception"
            };
            var result = new StringBuilder(category);
            if (exception is HttpRequestException http)
            {
                var status = http.StatusCode is { } code ? (int)code : 0;
                var raw = http.Message;
                // Some HTTP implementations provide only a leading status, not StatusCode.
                if (status == 0 && raw.Length >= 3 && (raw.Length == 3 || char.IsWhiteSpace(raw[3]))
                    && int.TryParse(raw.AsSpan(0, 3), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var leadingStatus)) status = leadingStatus;
                if (status is >= 100 and <= 599) result.Append(" HTTP ").Append(status);
            }
            var redacted = RedactText(exception.Message);
            if (redacted.Contains("System.Exception:", StringComparison.Ordinal)) result.Append(" | System.Exception:");
            if (redacted.Contains("server response:", StringComparison.OrdinalIgnoreCase)) result.Append(" | server response:");
            foreach (var marker in new[] { "[SECRET_REDACTED]", "[URL_REDACTED]", "[CREDENTIAL_REDACTED]" })
                if (redacted.Contains(marker, StringComparison.Ordinal)) result.Append(' ').Append(marker);
            return result.Append(" [DIAGNOSTIC_REDACTED]").ToString();
        }
        catch { return "System.Exception [DIAGNOSTIC_REDACTED]"; }
    }

    private static string RedactParameters(string text)
    {
        var output = new StringBuilder(text.Length);
        var copied = 0;
        for (var index = 0; index < text.Length;)
        {
            var start = index;
            string key;
            int end;
            if (text[index] is '"' or '\'')
            {
                end = QuotedEnd(text, index);
                if (end - index > MaximumUrlLength) throw new FormatException();
                key = text[index] == '"'
                    ? JsonSerializer.Deserialize<string>(text[index..end]) ?? ""
                    : text[(index + 1)..(end - 1)];
            }
            else if (IsKeyCharacter(text[index]) && (index == 0 || !IsKeyCharacter(text[index - 1])))
            {
                end = index + 1;
                while (end < text.Length && IsKeyCharacter(text[end])) end++;
                if (end - index > MaximumUrlLength) throw new FormatException();
                key = text[index..end];
            }
            else { index++; continue; }

            var separator = end;
            while (separator < text.Length && char.IsWhiteSpace(text[separator])) separator++;
            if (separator == text.Length || text[separator] is not (':' or '='))
            {
                index = end;
                continue;
            }
            if (TryDecode(key, out var decoded) && !decoded.Any(char.IsControl) && !IsSecretKey(decoded))
            {
                // Do not consume a nonsecret label's value: it may contain another exception/key.
                index = end;
                continue;
            }
            var valueEnd = separator + 1;
            while (valueEnd < text.Length && char.IsWhiteSpace(text[valueEnd])) valueEnd++;
            if (valueEnd < text.Length && text[valueEnd] is '"' or '\'') valueEnd = QuotedEnd(text, valueEnd);
            else
                while (valueEnd < text.Length && !char.IsWhiteSpace(text[valueEnd])
                    && text[valueEnd] is not (',' or ';' or '&' or '<' or '>' or '}' or ']')) valueEnd++;
            output.Append(text, copied, start - copied).Append("[SECRET_REDACTED]");
            copied = valueEnd;
            index = valueEnd;
        }
        return output.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool IsKeyCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '.' or '%' or '+' or '-';

    private static int QuotedEnd(string text, int start)
    {
        var quote = text[start];
        for (var index = start + 1; index < text.Length; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == quote) return index + 1;
        }
        throw new FormatException();
    }

    private static bool TrySafePage(string? value, out string safe)
    {
        safe = "";
        if (!TryAbsoluteHttp(value, out var uri) || uri.UserInfo.Length != 0
            || !TryDecode(value!, out var decoded) || decoded.Any(char.IsControl)
            || !TryDecode(uri.AbsolutePath, out var path)) return false;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Any(char.IsControl) || IsSecretKey(segment)) return false;
            // Opaque path identifiers can be credentials. Prefer an explicit public page.
            if (IsOpaqueIdentifier(segment)) return false;
        }
        if (ContainsSecretParameters(uri.Query.TrimStart('?')) || ContainsSecretParameters(uri.Fragment.TrimStart('#'))) return false;
        safe = uri.AbsoluteUri;
        return true;
    }

    private static bool ContainsSecretParameters(string parameters)
    {
        if (!TryDecode(parameters, out var decoded)) return true;
        foreach (var part in decoded.Split(['&', ';', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = pair[0].Replace('+', ' ');
            if (key.Any(char.IsControl) || IsSecretKey(key)) return true;
            if (pair.Length == 2 && (IsOpaqueIdentifier(pair[1]) || IsSecretKey(pair[1]))) return true;
        }
        return false;
    }

    private static bool IsOpaqueIdentifier(string value)
    {
        var compact = value.Replace("-", "").Replace("_", "");
        return compact.Length >= 32 && compact.Any(char.IsLetter) && compact.Any(char.IsDigit)
            && compact.All(char.IsLetterOrDigit);
    }

    private static bool IsSecretKey(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        var words = value.Split(['-', '_', '.', ' ', ':', '='], StringSplitOptions.RemoveEmptyEntries);
        return SecretKeys.Contains(normalized) || words.Any(SecretKeys.Contains)
            || normalized.StartsWith("auth", StringComparison.OrdinalIgnoreCase)
            || new[] { "token", "secret", "password", "session", "signature", "apikey", "credential", "passwd" }
                .Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = value;
        if (value.Length > MaximumUrlLength) return false;
        for (var pass = 0; pass < 3; pass++)
        {
            if (!decoded.Contains('%')) return true;
            for (var i = 0; i < decoded.Length; i++)
                if (decoded[i] == '%' && (i + 2 >= decoded.Length || !Uri.IsHexDigit(decoded[i + 1]) || !Uri.IsHexDigit(decoded[i + 2]))) return false;
            decoded = Uri.UnescapeDataString(decoded);
        }
        return !decoded.Contains('%');
    }

    private static bool TryOrigin(string? value, out string root)
    {
        root = "";
        if (!TryAbsoluteHttp(value, out var uri)) return false;
        root = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/")
        { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return true;
    }

    private static bool TryAbsoluteHttp(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlLength || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value.Contains('\\')) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }
}
