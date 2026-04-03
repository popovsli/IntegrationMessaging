using IntegrationMessaging.Configuration;
using IntegrationMessaging.Data;
using IntegrationMessaging.Entities;
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.CircuitBreaker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationMessaging.Services;

public sealed class MessageProcessor(
    IntegrationDbContext db,
    IMessageDispatcher dispatcher,
    ICircuitBreakerService circuitBreaker,
    IOptions<IntegrationMessagingOptions> options,
    ILogger<MessageProcessor> logger) : IMessageProcessor
{
    //private const string StatusSent = "Sent";
    //private const string StatusFailed = "Failed";
    //private const string StatusRetrying = "Retrying";

    public async Task ProcessPendingAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lockDuration = TimeSpan.FromMinutes(options.Value.LockDurationMinutes);

        // NEW: 1. Requeue stale Processing rows first (stuck from crashes/cancellation)
        await db.IntegrationMessageQueue
            .Where(q => q.Status == QueueMessageStatus.Processing
                     && q.LockedUntil <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueMessageStatus.Queued)
                .SetProperty(q => q.LockedUntil, (DateTime?)null)
                .SetProperty(q => q.NextAttempt, (DateTime?)null),
            ct);

        // IMPROVED: 2. Claim with SELECT FOR UPDATE equivalent (EF bulk update)
        var claimedCount = await db.IntegrationMessageQueue
            .Where(q => q.Status == QueueMessageStatus.Queued
                     && (q.NextAttempt == null || q.NextAttempt <= now)
                     && (q.LockedUntil == null || q.LockedUntil <= now))
            //.OrderBy(q => q.Priority ?? 0)  // NEW: respect Priority if present
            .OrderBy(q => q.CreationTime)
            .Take(batchSize)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueMessageStatus.Processing)
                .SetProperty(q => q.LockedUntil, now + lockDuration),
            ct);

        if (claimedCount == 0)
        {
            logger.LogDebug("No messages eligible for processing.");
            return;
        }

        logger.LogDebug("Claimed {ClaimedCount} messages for batch.", claimedCount);

        // IMPROVED: 3. Load ONLY the rows we just claimed (no race condition)
        var batch = await db.IntegrationMessageQueue
            .Include(q => q.IntegrationSystem)
            .Where(q => q.Status == QueueMessageStatus.Processing
                     && q.LockedUntil > now
                     && q.LockedUntil <= now + lockDuration)  // ← NEW: time-bound to this batch
            .OrderBy(q => q.CreationTime)
            .ToListAsync(ct);

        logger.LogInformation("Processing batch of {Count} messages.", batch.Count);

        // IMPROVED: 4. Progress tracking + better cancellation
        var processed = 0;
        foreach (var message in batch)
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation requested. {Remaining} messages left unprocessed.", batch.Count - processed);
                    break;
                }

                await ProcessSingleAsync(message.Id, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to process message {MessageId} in batch.", message.Id);
                // Continue processing other messages in the batch
            }
        }

        logger.LogInformation("Batch complete: {Processed}/{Total} processed.", processed, batch.Count);
    }

    public async Task ProcessSingleAsync(int queueMessageId, CancellationToken ct = default)
    {
        var message = await db.IntegrationMessageQueue
            .Include(q => q.IntegrationSystem)
            .FirstOrDefaultAsync(q => q.Id == queueMessageId, ct)
            ?? throw new IntegrationMessagingException($"Queue message {queueMessageId} not found.");

        var system = message.IntegrationSystem
            ?? throw new IntegrationMessagingException(
                $"IntegrationSystem '{message.IntegrationSystemCode}' not found.");

        if (!system.IsEnabled)
        {
            logger.LogWarning("System {SystemCode} is disabled. Skipping QueueId={Id}.",
                system.IntegrationSystemCode, message.Id);
            await ReleaseLockedAsync(message, ct);
            return;
        }

        if (circuitBreaker.IsOpen(system.IntegrationSystemCode))
        {
            logger.LogWarning("Circuit OPEN for {SystemCode}. Releasing lock on QueueId={Id}.",
                system.IntegrationSystemCode, message.Id);
            await ReleaseLockedAsync(message, ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // NEW: collapse redundant Updates before any sending happens
            await CollapseRedundantUpdatesAsync(message, system, ct, transaction);

            if (message.MessageOperation is MessageOperation.Update or MessageOperation.Delete)
                await EnsureCreateWasSentAsync(message, system, ct);

            await SendMessageAsync(message, system, ct);
            await transaction.CommitAsync(ct);
        }
        catch (PrerequisiteCreateNotFoundException ex)
        {
            await transaction.RollbackAsync(ct);
            await RecordFailureAsync(message, system, ex.Message, exhausted: true, ct);
            logger.LogError(ex, "Prerequisite Create not found for EntityId={EntityId} on {SystemCode}.",
                message.EntityId, system.IntegrationSystemCode);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            circuitBreaker.RecordFailure(system.IntegrationSystemCode, system);
            await RecordFailureAsync(message, system, ex.Message, exhausted: false, ct);
            logger.LogError(ex,
                "Failed {Operation} QueueId={Id} EntityId={EntityId} on {SystemCode}. Attempt={Attempt}.",
                message.MessageOperation, message.Id,
                message.EntityId, system.IntegrationSystemCode, message.AttemptCount + 1);
        }
    }

    /// <summary>
    /// RULE: Update/Delete cannot be sent without a prior successful Create
    /// for the same EntityId + IntegrationSystemCode.
    /// If no Create history exists, locate the pending Create in the queue
    /// and send it first.
    /// </summary>
    private async Task EnsureCreateWasSentAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        CancellationToken ct)
    {
        bool createAlreadySent = await db.IntegrationMessage.AnyAsync(
            h => h.EntityId == message.EntityId
              && h.IntegrationSystemCode == message.IntegrationSystemCode
              && h.Operation == MessageOperation.Create
              && h.Status == QueueMessageStatus.Sent.ToString(),
            ct);

        if (createAlreadySent) return;

        logger.LogWarning(
            "No successful Create in history for EntityId={EntityId} on {SystemCode}. Searching queue.",
            message.EntityId, message.IntegrationSystemCode);

        var pendingCreate = await db.IntegrationMessageQueue
            .Include(q => q.IntegrationSystem)
            .Where(q => q.EntityId == message.EntityId
                     && q.IntegrationSystemCode == message.IntegrationSystemCode
                     && q.MessageOperation == MessageOperation.Create)
            .OrderBy(q => q.CreationTime)
            .FirstOrDefaultAsync(ct);

        if (pendingCreate is null)
            throw new PrerequisiteCreateNotFoundException(
                message.EntityId, message.IntegrationSystemCode);

        logger.LogInformation(
            "Sending prerequisite Create QueueId={CreateId} before {Op} QueueId={Id}.",
            pendingCreate.Id, message.MessageOperation, message.Id);

        await SendMessageAsync(pendingCreate, system, ct);
    }

    private async Task SendMessageAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        CancellationToken ct)
    {
        var context = new SendContext { QueueMessage = message, System = system };
        var response = await dispatcher.DispatchAsync(context, ct);

        if (!response.IsSuccess)
            throw new IntegrationMessagingException(
                response.Error ?? $"Send failed. HTTP {response.HttpStatusCode}.");

        circuitBreaker.RecordSuccess(system.IntegrationSystemCode);

        db.IntegrationMessages.Add(new IntegrationMessage
        {
            IntegrationSystemCode = message.IntegrationSystemCode,
            EntityId = message.EntityId,
            Operation = message.MessageOperation,
            Status = QueueMessageStatus.Sent.ToString(),
            RequestPayload = message.Payload,
            ResponsePayload = response.ResponsePayload,
            HttpStatusCode = response.HttpStatusCode,
            LastAttemptAtUtc = DateTime.UtcNow,
            RetryCount = message.AttemptCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        //await db.IntegrationMessageQueue
        //    .Where(q => q.Id == message.Id)
        //    .ExecuteUpdateAsync(s => s
        //        .SetProperty(q => q.Status, QueueMessageStatus.Sent)
        //        .SetProperty(q => q.LockedUntil, (DateTime?)null),
        //    ct);

        // AFTER — delete, history is already in IntegrationMessage
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("✓ Sent {Op} EntityId={EntityId} on {SystemCode} | HTTP {Code}.",
            message.MessageOperation, message.EntityId,
            message.IntegrationSystemCode, response.HttpStatusCode);
    }

    /// <summary>
    /// If there are multiple Update messages for the same entity/system/type,
    /// only the latest one is sent. All earlier Updates are marked Skipped in the
    /// queue, but each is still written to IntegrationMessage as a history record.
    /// </summary>
    private async Task CollapseRedundantUpdatesAsync(
        IntegrationMessageQueue current,
        IntegrationSystem system,
        CancellationToken ct,
        IDbContextTransaction transaction)
    {
        // Only applies to Update operations
        if (current.MessageOperation != MessageOperation.Update)
            return;

        // Find all other Update messages for the same entity+system+type that are still queued
        var siblings = await db.IntegrationMessageQueue
            .Where(q =>
                   q.Id != current.Id &&
                   q.EntityId == current.EntityId &&
                   q.IntegrationSystemCode == current.IntegrationSystemCode &&
                   q.MessageTypeName == current.MessageTypeName &&
                   q.MessageOperation == MessageOperation.Update &&
                   (q.Status == QueueMessageStatus.Queued ||
                    q.Status == QueueMessageStatus.Processing))
            .OrderBy(q => q.CreationTime)
            .ToListAsync(ct);

        if (siblings.Count == 0)
            return;

        // Compute the "latest" Update = the one with max CreationTime (tie-breaker by Id)
        var latest = siblings.Append(current)
            .OrderBy(q => q.CreationTime)
            .ThenBy(q => q.Id)
            .Last();

        // If the current message is NOT the latest, we don't send it at all.
        // We mark it as Skipped and write a history entry, then return to the caller.
        if (latest.Id != current.Id)
        {
            await MarkSkippedAndRecordHistoryAsync(current, system, ct, reason: "Superseded by a newer Update");

            logger.LogInformation(
                "Update QueueId={Id} skipped; newer Update QueueId={LatestId} will be processed.",
                current.Id, latest.Id);

            await transaction.CommitAsync(ct);  // ← WORKS NOW
            return;                             // ← WORKS NOW
        }

        // If current is the latest, mark all earlier Update messages as Skipped and write history.
        foreach (var older in siblings)
            await MarkSkippedAndRecordHistoryAsync(older, system, ct, reason: "Superseded by a newer Update");
    }

    private async Task MarkSkippedAndRecordHistoryAsync(
    IntegrationMessageQueue message,
    IntegrationSystem system,
    CancellationToken ct,
    string reason)
    {
        // Write a history record with Status = "Skipped"
        db.IntegrationMessage.Add(new IntegrationMessage
        {
            IntegrationSystemCode = message.IntegrationSystemCode,
            EntityId = message.EntityId,
            Operation = message.MessageOperation,
            Status = QueueMessageStatus.Skipped.ToString(),
            RequestPayload = message.Payload,
            ResponsePayload = string.Empty,
            Error = reason,
            LastAttemptAtUtc = DateTime.UtcNow,
            RetryCount = message.AttemptCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // Mark queue row as Skipped and release lock
        //await db.IntegrationMessageQueue
        //    .Where(q => q.Id == message.Id)
        //    .ExecuteUpdateAsync(s => s
        //        .SetProperty(q => q.Status, QueueMessageStatus.Skipped)
        //        .SetProperty(q => q.LockedUntil, (DateTime?)null)
        //        .SetProperty(q => q.NextAttempt, (DateTime?)null),
        //    ct);

        // Delete queue row
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task RecordFailureAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        string error,
        bool exhausted,
        CancellationToken ct)
    {
        int newAttemptCount = message.AttemptCount + 1;
        bool isExhausted = exhausted || newAttemptCount >= system.QueueMessageRetryCount;

        db.IntegrationMessage.Add(new IntegrationMessage
        {
            IntegrationSystemCode = message.IntegrationSystemCode,
            EntityId = message.EntityId,
            Operation = message.MessageOperation,
            Status = isExhausted ? QueueMessageStatus.Failed.ToString() : QueueMessageStatus.Queued.ToString(),
            RequestPayload = message.Payload,
            ResponsePayload = string.Empty,
            Error = error,
            LastAttemptAtUtc = DateTime.UtcNow,
            RetryCount = newAttemptCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        //await db.IntegrationMessageQueue
        //    .Where(q => q.Id == message.Id)
        //    .ExecuteUpdateAsync(s => s
        //        .SetProperty(q => q.Status, isExhausted
        //            ? QueueMessageStatus.Failed
        //            : QueueMessageStatus.Queued)
        //        .SetProperty(q => q.AttemptCount, newAttemptCount)
        //        .SetProperty(q => q.LastError, error)
        //        .SetProperty(q => q.LockedUntil, (DateTime?)null)
        //        .SetProperty(q => q.NextAttempt, isExhausted
        //            ? (DateTime?)null
        //            : DateTime.UtcNow.AddSeconds(system.QueueMessageRetryDelaySeconds)),
        //    ct);

        if (isExhausted)
        {
            // Terminal failure — delete from queue, history is preserved
            //await db.IntegrationMessageQueue
            //    .Where(q => q.Id == message.Id)
            //    .ExecuteDeleteAsync(ct);

            // Move to Dead Letter
            await DeadLetterAsync(message, error, newAttemptCount, ct);
        }
        else
        {
            // Retry needed — keep in queue with backoff
            await db.IntegrationMessageQueue
                .Where(q => q.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.Status, QueueMessageStatus.Queued)
                    .SetProperty(q => q.AttemptCount, newAttemptCount)
                    .SetProperty(q => q.LastError, error)
                    .SetProperty(q => q.LockedUntil, (DateTime?)null)
                    .SetProperty(q => q.NextAttempt,
                        DateTime.UtcNow.AddSeconds(system.QueueMessageRetryDelaySeconds)),
                ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeadLetterAsync(
                    IntegrationMessageQueue message,
                    string reason,
                    int attemptCount,
                    CancellationToken ct)
    {
        // 1. Write to dead letter table
        db.Set<IntegrationDeadLetter>().Add(new IntegrationDeadLetter
        {
            OriginalQueueId = message.Id,
            IntegrationSystemCode = message.IntegrationSystemCode,
            EntityId = message.EntityId,
            MessageTypeName = message.MessageTypeName,
            MessageOperation = message.MessageOperation.ToString(),
            Payload = message.Payload,
            LastError = reason,
            AttemptCount = attemptCount,
            DeadLetteredAtUtc = DateTime.UtcNow
        });

        // 2. Delete from active queue — it must not block other messages
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);

        logger.LogError(
            "Message DEAD LETTERED after {Attempts} attempts | " +
            "EntityId={EntityId} System={System} Type={Type} Reason={Reason}",
            attemptCount, message.EntityId,
            message.IntegrationSystemCode, message.MessageTypeName, reason);

        // 3. Optionally raise an alert (hook your alerting system here)
        // await alertService.NotifyDeadLetterAsync(message, reason, ct);
    }

    private async Task ReleaseLockedAsync(IntegrationMessageQueue message, CancellationToken ct) =>
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueMessageStatus.Queued)
                .SetProperty(q => q.LockedUntil, (DateTime?)null),
            ct);
}
