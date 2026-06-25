namespace WebhookRelay.Core.Services;

// Exponential backoff for failed deliveries, capped at 1 hour. A delivery that fails
// MaxAttempts times is abandoned (marked Dead).
public static class BackoffPolicy
{
    public const int MaxAttempts = 8;

    public static DateTime NextAttempt(int attempts, DateTime now)
    {
        var seconds = Math.Min(Math.Pow(2, attempts), 3600);
        return now.AddSeconds(seconds);
    }
}
