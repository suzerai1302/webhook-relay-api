using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Core.Services;

namespace WebhookRelay.Infrastructure;

// One pass of the delivery pipeline: claim due deliveries, POST them to their endpoint
// with a signed payload, and record the outcome (succeed / retry with backoff / dead).
// Runs with no tenant context (background), so all queries ignore the tenant query filter.
public class DeliveryProcessor
{
    private readonly WebhookRelayDbContext _db;
    private readonly IHttpDelivery _http;
    private readonly IClock _clock;

    public DeliveryProcessor(WebhookRelayDbContext db, IHttpDelivery http, IClock clock)
    {
        _db = db;
        _http = http;
        _clock = clock;
    }

    public async Task ProcessDueAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // Claim due deliveries and mark them Delivering so concurrent workers don't double-send.
        // Postgres uses row locks (FOR UPDATE SKIP LOCKED); SQLite tests fall back to a plain
        // claim inside the ambient transaction.
        var due = await ClaimDueAsync(now, ct);

        foreach (var delivery in due)
        {
            var endpoint = await _db.Endpoints.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, ct);
            var ev = await _db.Events.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.Id == delivery.EventId, ct);
            if (endpoint is null || ev is null) continue;

            var signature = SignatureService.Sign(endpoint.SigningSecret, ev.Payload);
            var headers = new Dictionary<string, string>
            {
                ["X-Webhook-Id"] = delivery.Id.ToString(),
                ["X-Webhook-Event"] = ev.Type,
                ["X-Webhook-Signature"] = signature,
            };

            DeliveryResult result;
            try
            {
                result = await _http.SendAsync(endpoint.Url, ev.Payload, headers, ct);
            }
            catch
            {
                result = new DeliveryResult(false, null, "delivery threw");
            }

            delivery.Attempts++;
            delivery.LastStatusCode = result.StatusCode;
            delivery.LastResponseSnippet = result.BodySnippet;
            delivery.UpdatedAt = now;

            if (result.Success)
            {
                delivery.Status = DeliveryStatus.Succeeded;
                delivery.NextAttemptAt = null;
            }
            else if (delivery.Attempts >= BackoffPolicy.MaxAttempts)
            {
                delivery.Status = DeliveryStatus.Dead;
                delivery.NextAttemptAt = null;
            }
            else
            {
                delivery.Status = DeliveryStatus.Failed;
                delivery.NextAttemptAt = BackoffPolicy.NextAttempt(delivery.Attempts, now);
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<List<Delivery>> ClaimDueAsync(DateTime now, CancellationToken ct)
    {
        List<Delivery> claimed;
        if (_db.Database.IsNpgsql())
        {
            // Lock + skip rows other workers already hold; safe for horizontal scaling.
            claimed = await _db.Deliveries
                .FromSqlRaw(
                    """
                    SELECT * FROM "Deliveries"
                    WHERE ("Status" = 0 OR "Status" = 3)
                      AND "NextAttemptAt" IS NOT NULL AND "NextAttemptAt" <= {0}
                    FOR UPDATE SKIP LOCKED
                    """, now)
                .IgnoreQueryFilters()
                .ToListAsync(ct);
        }
        else
        {
            claimed = await _db.Deliveries.IgnoreQueryFilters()
                .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed)
                            && d.NextAttemptAt != null && d.NextAttemptAt <= now)
                .ToListAsync(ct);
        }

        foreach (var d in claimed)
        {
            d.Status = DeliveryStatus.Delivering;
            d.UpdatedAt = now;
        }
        if (claimed.Count > 0) await _db.SaveChangesAsync(ct);
        return claimed;
    }
}
