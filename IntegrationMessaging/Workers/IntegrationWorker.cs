using IntegrationMessaging.Configuration;
using IntegrationMessaging.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationMessaging.Workers;

public sealed class IntegrationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<IntegrationMessagingOptions> options,
    ILogger<IntegrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("IntegrationWorker started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IMessageProcessor>();
                await processor.ProcessPendingAsync(options.Value.BatchSize, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in IntegrationWorker tick.");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), ct);
        }

        logger.LogInformation("IntegrationWorker stopped.");
    }
}
