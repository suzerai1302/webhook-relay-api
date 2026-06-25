using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.Tests.Fakes;

// Controllable clock so backoff/next-attempt times are deterministic in tests.
public class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
