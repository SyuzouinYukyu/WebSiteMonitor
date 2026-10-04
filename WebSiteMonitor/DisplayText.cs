using WebSiteMonitor.Core;

namespace WebSiteMonitor;

// Display only: never feed these strings back into hashes, persistence or redaction.
internal static class DisplayText
{
    internal static string Content(string? text) => Markers(NotificationUrl.RedactText(text));
    internal static string Markers(string text) => text
        .Replace("[DIAGNOSTIC_REDACTED]", "安全上の理由により内容を非表示", StringComparison.Ordinal)
        .Replace("[URL_REDACTED]", "URLを非表示", StringComparison.Ordinal)
        .Replace("[SECRET_REDACTED]", "機密情報を非表示", StringComparison.Ordinal)
        .Replace("[CREDENTIAL_REDACTED]", "認証情報を非表示", StringComparison.Ordinal);

    internal static string Exception(Exception exception) => Diagnostic(NotificationUrl.SafeException(exception));
    internal static string InputError(Exception exception)
    {
        // Syntax validators wrap parser exceptions. Do not display their untrusted,
        // often English bodies; keep only our fixed validation heading.
        foreach (var heading in new[] { "CSS Selectorの構文が正しくありません", "XPathの構文が正しくありません", "正規表現の構文が正しくありません" })
            if (exception is ArgumentException && exception.InnerException is not null && exception.Message.StartsWith(heading, StringComparison.Ordinal))
                return heading.Replace("CSS Selector", "CSSセレクター", StringComparison.Ordinal) + "。";
        return exception is ArgumentException ? Content(exception.Message).Replace("CSS Selector", "CSSセレクター", StringComparison.Ordinal) : Exception(exception);
    }
    internal static string SoundError(SoundPlaybackResult result)
        => result.Exception is null ? Diagnostic(result.Message)
        : Exception(result.Exception) + "\n通知音ファイルと外部FFmpegのPATH登録を確認してください。DRM保護された音源は再生できません。";
    internal static string Diagnostic(string text)
    {
        var safe = Content(text);
        // Only app diagnostics call this method. Website text uses Content instead.
        foreach (var (source, target) in new (string, string)[] {
            ("HttpRequestException", "HTTP通信に失敗しました"),
            ("OperationCanceledException", "処理を中止しました"),
            ("TimeoutException", "通信がタイムアウトしました"),
            ("UnauthorizedAccessException", "ファイルへのアクセスが拒否されました"),
            ("InvalidOperationException", "処理を実行できませんでした"),
            ("ArgumentException", "入力内容を確認してください"),
            ("IOException", "ファイルの読み書きに失敗しました"),
            ("FormatException", "データ形式を確認してください"),
            ("JsonException", "設定データを読み取れませんでした"),
            ("XmlException", "RSS / Atomのデータを読み取れませんでした"),
            ("System.Exception:", "エラー："), ("System.Exception", "処理に失敗しました"),
            ("server response:", "サーバー応答："),
            ("Too Many Requests", "アクセス回数が制限されています"),
            ("Forbidden", "アクセスが拒否されました"), ("Unauthorized", "認証が必要です"),
            ("Not Found", "対象が見つかりません"),
            ("Cancelled", "処理を中止しました"), ("Canceled", "処理を中止しました"),
            ("Timeout", "通信がタイムアウトしました") }) safe = safe.Replace(source, target, StringComparison.Ordinal);
        foreach (var (status, description) in new (int, string)[] {
            (401, "認証が必要です"), (403, "アクセスが拒否されました"),
            (404, "対象が見つかりません"), (429, "アクセス回数が制限されています") })
            safe = safe.Replace($"HTTP {status}", $"HTTP {status}：{description}", StringComparison.Ordinal);
        return safe;
    }
}

internal static class ProductInfo
{
    internal static string Version => typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "不明";
    internal static string Title => "WebSite Monitor v" + Version;
}
