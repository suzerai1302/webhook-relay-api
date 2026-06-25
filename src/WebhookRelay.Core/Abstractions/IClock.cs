namespace WebhookRelay.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}
