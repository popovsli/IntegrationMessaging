// Services/Clients/Base/TypedSoapClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients;
using IntegrationMessaging.Services.Clients.Soap;
using Microsoft.Extensions.Logging;
using System.ServiceModel;

public abstract class TypedSoapClientBase<TContract>(
    ISoapChannelFactoryManager<TContract> factoryManager,  // ← actually used now
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
        var url = BuildUrl(system, request);
        logger.LogDebug("{Client} → {Url}", GetType().Name, url);

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

            return IntegrationResponse.Ok(payload);  // null is fine — Ok() accepts null
        }
        catch (FaultException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            logger.LogWarning("{Client} SOAP Fault: {Message}", GetType().Name, ex.Message);
            return IntegrationResponse.FaultFromException(ex);
        }
        catch (CommunicationException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);  // ← manager owns this
            logger.LogError(ex, "{Client} CommunicationException", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF communication failed — {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            ((IClientChannel?)channel)?.Abort();
            logger.LogError(ex, "{Client} Timeout", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF timed out — {ex.Message}", ex);
        }
        catch (IntegrationMessagingException)
        {
            ((IClientChannel?)channel)?.Abort();
            throw;
        }
        catch (Exception ex)
        {
            ((IClientChannel?)channel)?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogError(ex, "{Client} unexpected error", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: unexpected error — {ex.Message}", ex);
        }
    }

    private static string BuildUrl(IntegrationSystem system, IntegrationRequest request)
    {
        var path = string.IsNullOrWhiteSpace(request.EndpointPath)
            ? system.EndpointPath : request.EndpointPath;
        return $"{system.BaseAddress.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static void SafeClose(IClientChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}