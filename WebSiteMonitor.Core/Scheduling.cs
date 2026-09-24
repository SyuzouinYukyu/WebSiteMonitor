namespace WebSiteMonitor.Core;

public static class ScheduleCalculator
{
    public static DateTimeOffset? NextDue(Site site, DateTimeOffset from) => site.ScheduleMode switch
    {
        ScheduleMode.Manual => null,
        ScheduleMode.Interval => (site.LastChecked ?? from).AddMinutes(Math.Clamp(site.IntervalMinutes, 5, 10080)) is var next && next > from ? next : from,
        ScheduleMode.Daily => NextDaily(site.DailyTime, from),
        _ => null
    };

    private static DateTimeOffset NextDaily(string value, DateTimeOffset from)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", out var time)) time = new TimeOnly(9, 0);
        var local = from.ToLocalTime();
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, time.Hour, time.Minute, 0, local.Offset);
        return candidate > local ? candidate : candidate.AddDays(1);
    }
}

public static class RetryPolicy
{
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);
    public static bool IsTransient(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    public static TimeSpan DelayForAttempt(int attempt, TimeSpan? retryAfter = null)
        => ClampRetryAfter(retryAfter) ?? TimeSpan.FromMilliseconds(Math.Min(5000, 350 * Math.Pow(2, attempt)) + Random.Shared.Next(40, 180));

    public static TimeSpan DelayForAttempt(int attempt, TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate, DateTimeOffset now)
        => ClampRetryAfter(retryAfterDelta) ?? ClampRetryAfter(retryAfterDate is null ? null : retryAfterDate.Value - now) ?? DelayForAttempt(attempt);

    public static TimeSpan? ClampRetryAfter(TimeSpan? value)
    {
        if (value is null) return null;
        if (value <= TimeSpan.Zero) return TimeSpan.Zero;
        return value > MaximumRetryAfter ? MaximumRetryAfter : value;
    }
}

public static class UpdateDecision
{
    public static CheckOutcome Decide(string? oldHash, string newHash) => oldHash is null ? CheckOutcome.BaselineCreated : oldHash == newHash ? CheckOutcome.Unchanged : CheckOutcome.Changed;
    public static bool ShouldNotify(string? lastNotifiedHash, string newHash, CheckOutcome outcome) => outcome == CheckOutcome.Changed && !string.Equals(lastNotifiedHash, newHash, StringComparison.Ordinal);
}