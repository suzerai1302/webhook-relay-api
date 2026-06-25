using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.Infrastructure;

public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
