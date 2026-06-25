using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;

namespace WebhookRelay.API;

// Resolves per-plan limits from configuration (Plans:Free:*, Plans:Pro:*), falling back to
// the documented defaults when a key is absent.
public class PlanCatalog : IPlanCatalog
{
    private readonly IConfiguration _config;

    public PlanCatalog(IConfiguration config) => _config = config;

    public PlanLimits Limits(Plan plan)
    {
        var section = _config.GetSection($"Plans:{plan}");
        var (defEndpoints, defEvents) = plan == Plan.Pro ? (20, 10000) : (2, 100);
        return new PlanLimits(
            section.GetValue("MaxEndpoints", defEndpoints),
            section.GetValue("MaxEventsPerDay", defEvents));
    }
}
