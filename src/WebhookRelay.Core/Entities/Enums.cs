namespace WebhookRelay.Core.Entities;

public enum Plan
{
    Free,
    Pro,
}

public enum DeliveryStatus
{
    Pending,
    Delivering,
    Succeeded,
    Failed,
    Dead,
}
