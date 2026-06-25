namespace WebhookRelay.Core.Entities;

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Hash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
