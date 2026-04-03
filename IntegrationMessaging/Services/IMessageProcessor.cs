namespace IntegrationMessaging.Services;

public interface IMessageProcessor
{
    Task ProcessPendingAsync(int batchSize = 50, CancellationToken ct = default);
    Task ProcessSingleAsync(int queueMessageId, CancellationToken ct = default);
}
