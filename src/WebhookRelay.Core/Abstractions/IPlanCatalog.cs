using WebhookRelay.Core.Entities;

namespace WebhookRelay.Core.Abstractions;

// Per-plan quota limits, sourced from configuration (Plans:Free:*, Plans:Pro:*).
public record PlanLimits(int MaxEndpoints, int MaxEventsPerDay);

public interface IPlanCatalog
{
    PlanLimits Limits(Plan plan);
}
