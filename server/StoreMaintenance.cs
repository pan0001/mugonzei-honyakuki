namespace Mugonzei.Server;

internal sealed class StoreMaintenance(Store store, ILogger<StoreMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { store.PruneExpiredData(); }
            catch (Exception error) { logger.LogError("Store maintenance failed: {ErrorType}", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
