using WebSiteMonitor.Core;

namespace WebSiteMonitor;

internal static class ConsecutiveErrorAlert
{
    internal static string? CreateMessage(CheckResult result, int threshold)
    {
        if (result.Outcome != CheckOutcome.Failed || threshold is < 1 or > 9999) return null;
        // UI delivery may be delayed: only this failure's committed snapshot is authoritative.
        if (result.CommittedConsecutiveErrors != threshold) return null;
        return $"サイト「{DisplayText.Content(result.Site.Name)}」で監視エラーが{result.CommittedConsecutiveErrors}回連続して発生しました。\n\nメイン画面の状態と監視設定を確認してください。";
    }
}
