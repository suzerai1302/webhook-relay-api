namespace WebhookRelay.Core.Entities;

public class Delivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid EndpointId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastResponseSnippet { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
