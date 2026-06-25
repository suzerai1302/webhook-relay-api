namespace WebhookRelay.Core.Abstractions;

// Outbound HTTP POST to a subscriber endpoint. Abstracted so the dispatcher can be
// driven deterministically in tests (a fake records calls and scripts results).
public interface IHttpDelivery
{
    Task<DeliveryResult> SendAsync(
        string url, string body, IDictionary<string, string> headers, CancellationToken ct);
}

public record DeliveryResult(bool Success, int? StatusCode, string? BodySnippet);
