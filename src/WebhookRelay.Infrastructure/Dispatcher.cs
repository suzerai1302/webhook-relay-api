using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebhookRelay.Infrastructure;

// Hosted loop that drives the delivery pipeline every Interval. DeliveryProcessor is
// scoped (owns a DbContext) so each tick resolves a fresh one. A failing tick is logged
// and the loop continues — one bad pass never kills the dispatcher.
public class Dispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<Dispatcher> _logger;
    private readonly TimeSpan _interval;

    public Dispatcher(IServiceScopeFactory scopes, ILogger<Dispatcher> logger, TimeSpan interval)
    {
        _scopes = scopes;
        _logger = logger;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            await RunSafelyAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessDueAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // shutting down — let the token end the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delivery dispatch pass failed; will retry next interval.");
        }
    }
}
