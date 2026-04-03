// Data/DatabaseInitializer.cs
// Called once at startup (before the host starts processing messages).
// Responsibilities:
//   1. Wait for the database to be reachable (with retries for container start lag)
//   2. Apply any pending EF Core migrations automatically
//   3. Run idempotent seed data

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Data;

public sealed class DatabaseInitializer(
    IntegrationDbContext db,
    ILogger<DatabaseInitializer> logger)
{
    private const int MaxRetries = 10;
    private const int RetryDelayMs = 2_000;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await WaitForDatabaseAsync(ct);
        await MigrateAsync(ct);
        await SeedAsync(ct);
    }

    // ── 1. Wait for DB ────────────────────────────────────────────────────

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var canConnect = await db.Database.CanConnectAsync(ct);
                if (canConnect)
                {
                    logger.LogInformation(
                        "Database reachable on attempt {Attempt}.", attempt);
                    return;
                }
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(
                    "Database not reachable (attempt {Attempt}/{Max}): {Message}. " +
                    "Retrying in {DelayMs}ms…",
                    attempt, MaxRetries, ex.Message, RetryDelayMs);
            }

            await Task.Delay(RetryDelayMs, ct);
        }

        throw new InvalidOperationException(
            $"Database not reachable after {MaxRetries} attempts. " +
            "Verify ConnectionStrings:IntegrationMessaging in appsettings.json.");
    }

    // ── 2. Migrate ────────────────────────────────────────────────────────

    private async Task MigrateAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("No pending migrations.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending migration(s): {Names}",
            pending.Count,
            string.Join(", ", pending));

        await db.Database.MigrateAsync(ct);

        logger.LogInformation("Migrations applied successfully.");
    }

    // ── 3. Seed ───────────────────────────────────────────────────────────

    private async Task SeedAsync(CancellationToken ct)
    {
        logger.LogInformation("Running seed data…");
        await SeedData.SeedAsync(db, ct);
        logger.LogInformation("Seed complete.");
    }
}
