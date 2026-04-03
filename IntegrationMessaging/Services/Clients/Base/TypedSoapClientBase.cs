// Services/Clients/Base/TypedSoapClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients;
using IntegrationMessaging.Services.Clients.Soap;
using IntegrationMessaging.Services.Resilience;
using Microsoft.Extensions.Logging;
using System.ServiceModel;

public abstract class TypedSoapClientBase<TContract>(
    ISoapChannelFactoryManager<TContract> factoryManager,
    IResiliencePipelineFactory pipelineFactory,
    ILogger logger) : IIntegrationClient
    where TContract : class
{
    protected abstract ChannelFactory<TContract> CreateFactory(
      string username, string password);

    /// <summary>
    /// Executes the typed WCF call.
    /// Return a serialized response string (JSON recommended) if the operation
    /// returns meaningful data. Return null for void/one-way operations.
    /// </summary>
    protected abstract Task<string?> InvokeContractAsync(
        TContract channel,
        IntegrationRequest request,
        SendContext context,
        CancellationToken ct);

    public async Task<IntegrationResponse> SendAsync(
      IntegrationRequest request,
      IntegrationSystem system,
      CancellationToken ct = default)
    {
        var pipeline = pipelineFactory.CreateSoapPipeline(system);
        try
        {
            return await pipeline.ExecuteAsync(
                async token => await ExecuteOnceAsync(request, system, token), ct);
        }
        catch (CommunicationException ex)
        {
            // Polly exhausted all retries — wrap for caller
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF communication failed for " +
                $"'{system.SystemName}' ({system.IntegrationSystemCode}) " +
                $"after {system.ClientRetryCount} retries — {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF timed out for " +
                $"'{system.SystemName}' ({system.IntegrationSystemCode}) " +
                $"after {system.ClientRetryCount} retries — {ex.Message}", ex);
        }
    }

    private async Task<IntegrationResponse> ExecuteOnceAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct = default)
    {
        var url = BuildUrl(system, request);

        logger.LogDebug("{Client} → {Url} | System={SystemName} [{SystemCode}]",
                   GetType().Name, url, system.SystemName, system.IntegrationSystemCode);

        TContract? channel = default;
        try
        {
            // Manager handles cache, fingerprint, invalidation — no duplicate logic
            var factory = factoryManager.GetOrCreate(system, CreateFactory);

            channel = factory.CreateChannel(new EndpointAddress(url));
            ((IClientChannel)channel).Open();

            var payload = await InvokeContractAsync(channel, request, request.Context!, ct);

            SafeClose((IClientChannel)channel);
            channel = default;

            logger.LogDebug("{Client} ← success | System={SystemName} [{SystemCode}]",
               GetType().Name, system.SystemName, system.IntegrationSystemCode);

            return IntegrationResponse.Ok(payload);  // null is fine — Ok() accepts null
        }
        catch (CommunicationException ex)
        {
            // Transient — invalidate factory, rethrow for Polly to retry
            ((IClientChannel?)channel)?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogWarning(ex,
                "{Client} CommunicationException (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;
        }
        catch (TimeoutException ex)
        {
            // Transient — rethrow for Polly to retry
            ((IClientChannel?)channel)?.Abort();
            logger.LogWarning(ex,
                "{Client} Timeout (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;
        }
        catch (IntegrationMessagingException)
        {
            // Config/contract error — never retry
            ((IClientChannel?)channel)?.Abort();
            throw;
        }
        catch (Exception ex)
        {
            ((IClientChannel?)channel)?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogError(ex,
                "{Client} unexpected error | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: unexpected error for " +
                $"'{system.SystemName}' ({system.IntegrationSystemCode}): {ex.Message}", ex);
        }
    }

    private static string BuildUrl(IntegrationSystem system, IntegrationRequest request)
        => UrlBuilder.Build(system, request);

    private static void SafeClose(IClientChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}