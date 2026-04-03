// Services/MessageProcessor.cs
// FIXES:
//   #1 — SaveChangesAsync removed from SendMessageAsync / MarkSkippedAndRecordHistoryAsync;
//          a single SaveChangesAsync is called inside the transaction in ProcessSingleAsync.
//   #2 — CollapseRedundantUpdatesAsync no longer calls CommitAsync internally;
//          it returns bool so the caller owns the commit.
//   #5 — Batch load filtered by a unique WorkerId stamp instead of a time window.
//   #6 — ProcessSingleAsync signature changed to accept the already-loaded entity;
//          the extra DB round-trip is eliminated.

using IntegrationMessaging.Configuration;
using IntegrationMessaging.Data;
using IntegrationMessaging.Entities;
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.CircuitBreaker;
using Microsoft.EntityFrameworkCore;
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
    // Unique per-process stamp — used to identify exactly which rows THIS worker claimed
    private static readonly Guid WorkerId = Guid.CreateVersion7();

    public async Task ProcessPendingAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lockDuration = TimeSpan.FromMinutes(options.Value.LockDurationMinutes);
        var lockUntil = now + lockDuration;

        // 1. Requeue stale Processing rows (stuck from crashes / cancellation)
        await db.IntegrationMessageQueue
            .Where(q => q.Status == QueueMessageStatus.Processing && q.LockedUntil <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueMessageStatus.Queued)
                .SetProperty(q => q.LockedUntil, (DateTime?)null)
                .SetProperty(q => q.NextAttempt, (DateTime?)null), ct);

        // 2. Claim batch — stamp with WorkerId so only THIS process loads these rows
        //    ExecuteUpdate does not participate in the EF change tracker;
        //    we match exactly by WorkerStamp in step 3 to avoid cross-worker contamination.
        var claimedCount = await db.IntegrationMessageQueue
            .Where(q => q.Status == QueueMessageStatus.Queued
                     && (q.NextAttempt == null || q.NextAttempt <= now)
                     && (q.LockedUntil == null || q.LockedUntil <= now))
            .OrderBy(q => q.CreationTime)
            .Take(batchSize)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueMessageStatus.Processing)
                .SetProperty(q => q.LockedUntil, lockUntil)
                .SetProperty(q => q.WorkerStamp, WorkerId), ct);    // ← unique stamp

        if (claimedCount == 0)
        {
            logger.LogDebug("No messages eligible for processing.");
            return;
        }

        logger.LogDebug("Claimed {Count} messages for worker {WorkerId}.", claimedCount, WorkerId);

        // 3. Load ONLY the rows stamped with THIS worker's id — no cross-worker leakage
        var batch = await db.IntegrationMessageQueue
            .Include(q => q.IntegrationSystem)
            .Where(q => q.Status == QueueMessageStatus.Processing
                     && q.WorkerStamp == WorkerId)
            .OrderBy(q => q.CreationTime)
            .ToListAsync(ct);

        logger.LogInformation("Processing batch of {Count} messages.", batch.Count);

        var processed = 0;
        foreach (var message in batch)
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogWarning("Cancellation requested. {Remaining} messages left unprocessed.",
                    batch.Count - processed);
                break;
            }

            try
            {
                // FIX #6: pass the already-loaded entity — no second DB round-trip
                await ProcessSingleAsync(message, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to process message {MessageId} in batch.", message.Id);
            }
        }

        logger.LogInformation("Batch complete: {Processed}/{Total} processed.", processed, batch.Count);
    }

    // FIX #6: accepts already-loaded entity instead of an Id
    public async Task ProcessSingleAsync(IntegrationMessageQueue message, CancellationToken ct = default)
    {
         //var message = await db.IntegrationMessageQueue
         //   .Include(q => q.IntegrationSystem)
         //   .FirstOrDefaultAsync(q => q.Id == queueMessageId, ct)
         //   ?? throw new IntegrationMessagingException($"Queue message {queueMessageId} not found.");

        var system = message.IntegrationSystem
            ?? throw new IntegrationMessagingException(
                $"IntegrationSystem '{message.IntegrationSystemCode}' not loaded on queue message {message.Id}.");

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
            // FIX #2: CollapseRedundantUpdatesAsync returns bool — it never commits internally
            var collapsed = await CollapseRedundantUpdatesAsync(message, system, ct);
            if (collapsed)
            {
                await db.SaveChangesAsync(ct);    // FIX #1: single SaveChanges in tx
                await transaction.CommitAsync(ct);
                return;
            }

            if (message.MessageOperation is MessageOperation.Update or MessageOperation.Delete)
                await EnsureCreateWasSentAsync(message, system, ct);

            await SendMessageAsync(message, system, ct);

            await db.SaveChangesAsync(ct);        // FIX #1: single SaveChanges in tx
            await transaction.CommitAsync(ct);
        }
        catch (PrerequisiteCreateNotFoundException ex)
        {
            await transaction.RollbackAsync(ct);
            await RecordFailureAsync(message, system, ex.Message, exhausted: true, ct);
            logger.LogError(ex,
                "Prerequisite Create not found for EntityId={EntityId} on {SystemCode}.",
                message.EntityId, system.IntegrationSystemCode);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            circuitBreaker.RecordFailure(system.IntegrationSystemCode, system);
            await RecordFailureAsync(message, system, ex.Message, exhausted: false, ct);
            logger.LogError(ex,
                "Failed {Operation} QueueId={Id} EntityId={EntityId} on {SystemCode}. Attempt={Attempt}.",
                message.MessageOperation, message.Id, message.EntityId,
                system.IntegrationSystemCode, message.AttemptCount + 1);
        }
    }

    // ── Core send ─────────────────────────────────────────────────────────

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

        // FIX #1: Add to tracker only — NO SaveChangesAsync here
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

        // ExecuteDeleteAsync runs immediately — intentional, within the open transaction
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteDeleteAsync(ct);

        // NO SaveChangesAsync here — caller saves once
        logger.LogInformation("✓ Sent {Op} EntityId={EntityId} on {SystemCode} | HTTP {Code}.",
            message.MessageOperation, message.EntityId,
            message.IntegrationSystemCode, response.HttpStatusCode);
    }

    // ── Collapse redundant updates ────────────────────────────────────────

    // FIX #2: returns bool — true means this message was skipped and changes are tracked.
    //         Caller is responsible for SaveChangesAsync + CommitAsync.
    private async Task<bool> CollapseRedundantUpdatesAsync(
        IntegrationMessageQueue current,
        IntegrationSystem system,
        CancellationToken ct)
    {
        if (current.MessageOperation != MessageOperation.Update)
            return false;

        var siblings = await db.IntegrationMessageQueue
            .Where(q => q.Id != current.Id
                     && q.EntityId == current.EntityId
                     && q.IntegrationSystemCode == current.IntegrationSystemCode
                     && q.MessageTypeName == current.MessageTypeName
                     && q.MessageOperation == MessageOperation.Update
                     && (q.Status == QueueMessageStatus.Queued
                      || q.Status == QueueMessageStatus.Processing))
            .OrderBy(q => q.CreationTime)
            .ToListAsync(ct);

        if (siblings.Count == 0) return false;

        var latest = siblings.Append(current)
            .OrderBy(q => q.CreationTime)
            .ThenBy(q => q.Id)
            .Last();

        if (latest.Id != current.Id)
        {
            // FIX #1: track only — no SaveChangesAsync
            await MarkSkippedAndRecordHistoryAsync(current, system, ct,
                reason: "Superseded by a newer Update");

            logger.LogInformation(
                "Update QueueId={Id} skipped; newer Update QueueId={LatestId} will be processed.",
                current.Id, latest.Id);

            return true; // caller commits
        }

        // Current is latest — mark all older siblings as skipped
        foreach (var older in siblings)
            await MarkSkippedAndRecordHistoryAsync(older, system, ct,
                reason: "Superseded by a newer Update");

        return false; // current should be sent normally
    }

    private async Task MarkSkippedAndRecordHistoryAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        CancellationToken ct,
        string reason)
    {
        // FIX #1: Add to tracker only — NO SaveChangesAsync here
        db.IntegrationMessages.Add(new IntegrationMessage
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

        // ExecuteDeleteAsync runs immediately — intentional
        await db.IntegrationMessageQueue
            .Where(q => q.Id == message.Id)
            .ExecuteDeleteAsync(ct);

        // NO SaveChangesAsync — caller saves once
    }

    // ── Prerequisite check ────────────────────────────────────────────────

    private async Task EnsureCreateWasSentAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        CancellationToken ct)
    {
        bool createAlreadySent = await db.IntegrationMessages.AnyAsync(
            h => h.EntityId == message.EntityId
              && h.IntegrationSystemCode == message.IntegrationSystemCode
              && h.Operation == MessageOperation.Create
              && h.Status == QueueMessageStatus.Sent.ToString(), ct);

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

    // ── Failure handling ──────────────────────────────────────────────────

    private async Task RecordFailureAsync(
        IntegrationMessageQueue message,
        IntegrationSystem system,
        string error,
        bool exhausted,
        CancellationToken ct)
    {
        int newAttemptCount = message.AttemptCount + 1;
        bool isExhausted = exhausted || newAttemptCount >= system.QueueMessageRetryCount;

        db.IntegrationMessages.Add(new IntegrationMessage
        {
            IntegrationSystemCode = message.IntegrationSystemCode,
            EntityId = message.EntityId,
            Operation = message.MessageOperation,
            Status = isExhausted
                                    ? QueueMessageStatus.Failed.ToString()
                                    : QueueMessageStatus.Queued.ToString(),
            RequestPayload = message.Payload,
            ResponsePayload = string.Empty,
            Error = error,
            LastAttemptAtUtc = DateTime.UtcNow,
            RetryCount = newAttemptCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        if (isExhausted)
        {
            await DeadLetterAsync(message, error, newAttemptCount, ct);
        }
        else
        {
            await db.IntegrationMessageQueue
                .Where(q => q.Id == message.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.Status, QueueMessageStatus.Queued)
                    .SetProperty(q => q.AttemptCount, newAttemptCount)
                    .SetProperty(q => q.LastError, error)
                    .SetProperty(q => q.LockedUntil, (DateTime?)null)
                    .SetProperty(q => q.NextAttempt,
                        DateTime.UtcNow.AddSeconds(system.QueueMessageRetryDelaySeconds)), ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeadLetterAsync(
        IntegrationMessageQueue message,
        string reason,
        int attemptCount,
        CancellationToken ct)
    {
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
                .SetProperty(q => q.LockedUntil, (DateTime?)null), ct);
}
