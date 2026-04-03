using IntegrationMessaging.Data;
using IntegrationMessaging.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntegrationMessaging.HealthChecks;

public sealed class IntegrationHealthCheck(IntegrationDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var stats = await db.IntegrationMessageQueue
                .GroupBy(q => q.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var data = stats.ToDictionary(
                x => x.Status.ToString(),
                x => (object)x.Count);

            var failedCount = stats
                .FirstOrDefault(s => s.Status == QueueMessageStatus.Failed)?.Count ?? 0;

            return failedCount > 100
                ? HealthCheckResult.Degraded($"{failedCount} messages in Failed status.", data: data)
                : HealthCheckResult.Healthy("Queue processing nominal.", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
