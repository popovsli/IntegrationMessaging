// Services/Clients/Base/TypedSoapClientBase.cs
// FIX #10: namespace declaration added (was missing — class fell into global namespace).
// Previous fixes already applied: goto removed, CommunicationException/TimeoutException
// rethrown bare inside ExecuteOnceAsync so Polly can retry them.

using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients.Soap;
using IntegrationMessaging.Services.Resilience;
using Microsoft.Extensions.Logging;
using System.ServiceModel;

namespace IntegrationMessaging.Services.Clients.Base;

public abstract class TypedSoapClientBase<TContract>(
    ISoapChannelFactoryManager<TContract> factoryManager,
    IResiliencePipelineFactory pipelineFactory,
    ILogger logger) : IIntegrationClient
    where TContract : class
{
    protected abstract ChannelFactory<TContract> CreateFactory(
        string username, string password);

    protected abstract Task<string?> InvokeContractAsync(
        TContract channel,
        IntegrationRequest request,
        SendContext context,
        CancellationToken ct);

    // ── Public entry point — Polly wraps ExecuteOnceAsync ────────────────

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

    // ── Single attempt — no retry logic here, Polly owns that ────────────

    private async Task<IntegrationResponse> ExecuteOnceAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct)
    {
        var url = UrlBuilder.Build(system, request);

        logger.LogDebug("{Client} → {Url} | System={SystemName} [{SystemCode}]",
            GetType().Name, url, system.SystemName, system.IntegrationSystemCode);

        TContract? channel = default;
        try
        {
            var factory = factoryManager.GetOrCreate(system, CreateFactory);

            channel = factory.CreateChannel(new EndpointAddress(url));
            ((IClientChannel)channel).Open();

            var payload = await InvokeContractAsync(
                channel, request, request.Context!, ct);

            SafeClose((IClientChannel)channel);
            channel = default;

            logger.LogDebug("{Client} ← success | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);

            return IntegrationResponse.Ok(payload);
        }
        catch (FaultException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            logger.LogWarning(
                "{Client} SOAP Fault: {Message} | System={SystemName} [{SystemCode}]",
                GetType().Name, ex.Message,
                system.SystemName, system.IntegrationSystemCode);
            return IntegrationResponse.FaultFromException(ex);
        }
        catch (CommunicationException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogWarning(ex,
                "{Client} CommunicationException (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;  // Polly retries; SendAsync wraps after exhaustion
        }
        catch (TimeoutException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            logger.LogWarning(ex,
                "{Client} Timeout (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;  // Polly retries
        }
        catch (IntegrationMessagingException)
        {
            ((IClientChannel?)channel)?.Abort();
            throw;  // config/contract error — never retry
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

    private static void SafeClose(IClientChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}
