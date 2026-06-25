namespace WebhookRelay.Core.Entities;

public class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
}
