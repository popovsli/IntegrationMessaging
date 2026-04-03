// Services/IDeadLetterService.cs
namespace IntegrationMessaging.Services;

public interface IDeadLetterService
{
    /// <summary>Requeue a single dead-lettered message.</summary>
    Task<int> RequeueAsync(
        int deadLetterId,
        string requeuedByUser,
        string? resolutionNote = null,
        CancellationToken ct = default);

    /// <summary>Requeue all unresolved dead letters for a system.</summary>
    Task<int> RequeueAllAsync(
        string integrationSystemCode,
        string requeuedByUser,
        string? resolutionNote = null,
        CancellationToken ct = default);

    /// <summary>
    /// Discard a dead-lettered message — marks it resolved without requeuing.
    /// Use when the message is invalid and should never be sent.
    /// </summary>
    Task DiscardAsync(
        int deadLetterId,
        string discardedByUser,
        string resolutionNote,
        CancellationToken ct = default);
}
