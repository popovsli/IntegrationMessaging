// Services/DeadLetterService.cs
using IntegrationMessaging.Data;
using IntegrationMessaging.Entities;
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services;

public sealed class DeadLetterService(
    IntegrationDbContext db,
    ILogger<DeadLetterService> logger) : IDeadLetterService
{
    public async Task<int> RequeueAsync(
        int deadLetterId,
        string requeuedByUser,
        string? resolutionNote = null,
        CancellationToken ct = default)
    {
        var deadLetter = await LoadUnresolvedAsync(deadLetterId, ct);

        await GuardAgainstDuplicateAsync(deadLetter, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var newQueueRow = BuildQueueRow(deadLetter, requeuedByUser);
            db.IntegrationMessageQueue.Add(newQueueRow);

            await MarkResolvedAsync(
                deadLetter,
                DeadLetterResolution.Requeued,
                requeuedByUser,
                resolutionNote ?? "Manually requeued for reprocessing",
                ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Dead letter {DlqId} requeued → new QueueId={QueueId} " +
                "| EntityId={EntityId} Operation={Operation} by {User}.",
                deadLetterId, newQueueRow.Id,
                deadLetter.EntityId, deadLetter.MessageOperation, requeuedByUser);

            return newQueueRow.Id;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> RequeueAllAsync(
        string integrationSystemCode,
        string requeuedByUser,
        string? resolutionNote = null,
        CancellationToken ct = default)
    {
        var unresolved = await db.IntegrationDeadLetters
            .Where(d => d.IntegrationSystemCode == integrationSystemCode
                     && d.ResolvedAtUtc == null)
            .OrderBy(d => d.DeadLetteredAtUtc)
            .ToListAsync(ct);

        if (unresolved.Count == 0)
        {
            logger.LogWarning(
                "No unresolved dead letters found for system {SystemCode}.",
                integrationSystemCode);
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var requeued = 0;
            foreach (var deadLetter in unresolved)
            {
                try
                {
                    await GuardAgainstDuplicateAsync(deadLetter, ct);

                    db.IntegrationMessageQueue.Add(
                        BuildQueueRow(deadLetter, requeuedByUser));

                    await MarkResolvedAsync(
                        deadLetter,
                        DeadLetterResolution.Requeued,
                        requeuedByUser,
                        resolutionNote ?? "Bulk requeue after system recovery",
                        ct);

                    requeued++;
                }
                catch (IntegrationMessagingException ex)
                {
                    // Already queued — skip without aborting the whole batch
                    logger.LogWarning(
                        "Skipping DLQ {Id}: {Reason}", deadLetter.Id, ex.Message);
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Bulk requeue: {Count}/{Total} dead letters requeued for {SystemCode} by {User}.",
                requeued, unresolved.Count, integrationSystemCode, requeuedByUser);

            return requeued;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DiscardAsync(
        int deadLetterId,
        string discardedByUser,
        string resolutionNote,
        CancellationToken ct = default)
    {
        var deadLetter = await LoadUnresolvedAsync(deadLetterId, ct);

        await MarkResolvedAsync(
            deadLetter,
            DeadLetterResolution.Discarded,
            discardedByUser,
            resolutionNote,
            ct);

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Dead letter {DlqId} DISCARDED by {User} | Reason: {Note}",
            deadLetterId, discardedByUser, resolutionNote);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<IntegrationDeadLetter> LoadUnresolvedAsync(
        int deadLetterId, CancellationToken ct)
    {
        var deadLetter = await db.IntegrationDeadLetters
            .FirstOrDefaultAsync(d => d.Id == deadLetterId, ct)
            ?? throw new IntegrationMessagingException(
                $"Dead letter {deadLetterId} not found.");

        if (deadLetter.ResolvedAtUtc is not null)
            throw new IntegrationMessagingException(
                $"Dead letter {deadLetterId} is already resolved " +
                $"({deadLetter.Resolution} by {deadLetter.ResolvedByUser} " +
                $"at {deadLetter.ResolvedAtUtc:u}).");

        return deadLetter;
    }

    /// <summary>
    /// Prevent double-requeue if an active queue row for the
    /// same entity+system+operation already exists.
    /// </summary>
    private async Task GuardAgainstDuplicateAsync(
        IntegrationDeadLetter deadLetter, CancellationToken ct)
    {
        var operation = Enum.Parse<MessageOperation>(deadLetter.MessageOperation);

        bool alreadyActive = await db.IntegrationMessageQueue.AnyAsync(
            q => q.EntityId == deadLetter.EntityId
              && q.IntegrationSystemCode == deadLetter.IntegrationSystemCode
              && q.MessageOperation == operation
              && (q.Status == QueueMessageStatus.Queued ||
                  q.Status == QueueMessageStatus.Processing),
            ct);

        if (alreadyActive)
            throw new IntegrationMessagingException(
                $"An active queue row already exists for EntityId={deadLetter.EntityId}, " +
                $"Operation={deadLetter.MessageOperation} on '{deadLetter.IntegrationSystemCode}'. " +
                "Requeue aborted to prevent duplicate sending.");
    }

    private static IntegrationMessageQueue BuildQueueRow(
        IntegrationDeadLetter deadLetter,
        string requeuedByUser) => new()
        {
            EntityId = deadLetter.EntityId,
            IntegrationSystemCode = deadLetter.IntegrationSystemCode,
            MessageOperation = Enum.Parse<MessageOperation>(deadLetter.MessageOperation),
            Payload = deadLetter.Payload,  // ← exact original payload
            Status = QueueMessageStatus.Queued,
            AttemptCount = 0,                   // ← fresh retry budget
            CreationTime = DateTime.UtcNow,
            MessageTypeName = deadLetter.MessageTypeName,
            RequeuedFromMessageId = deadLetter.IntegrationMessageId,
            RequeuedBy = requeuedByUser,
            RequeuedAtUtc = DateTime.UtcNow
        };

    private static Task MarkResolvedAsync(
        IntegrationDeadLetter deadLetter,
        DeadLetterResolution resolution,
        string resolvedByUser,
        string resolutionNote,
        CancellationToken _)
    {
        deadLetter.ResolvedAtUtc = DateTime.UtcNow;
        deadLetter.ResolvedByUser = resolvedByUser;
        deadLetter.ResolutionNote = resolutionNote;
        deadLetter.Resolution = resolution;
        return Task.CompletedTask;
    }
}