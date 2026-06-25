namespace WebhookRelay.Core.Entities;

public class Endpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Url { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? EventTypeFilter { get; set; }
    public DateTime CreatedAt { get; set; }
}
