// Services/Clients/Base/SoapIntegrationClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients.Soap;
using IntegrationMessaging.Services.Resilience;
using Microsoft.Extensions.Logging;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Xml;

namespace IntegrationMessaging.Services.Clients.Base;

public abstract class SoapIntegrationClientBase(
    ISoapChannelFactoryManager<IRequestChannel> factoryManager,
    IResiliencePipelineFactory pipelineFactory,
    ILogger logger) : IIntegrationClient
{
    // ── Subclass configures the WCF binding entirely in code ─────────────

    /// <summary>
    /// Build a fully configured ChannelFactory — binding + credentials.
    /// Called only when no cached factory exists or config changed in DB.
    /// </summary>
    protected abstract ChannelFactory<IRequestChannel> CreateFactory(
        string username, string password);


    // ── Public entry point — Polly wraps ExecuteOnceAsync ────────────────

    public async Task<IntegrationResponse> SendAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SoapAction))
            throw new IntegrationMessagingException(
                $"{GetType().Name} requires SoapAction on IntegrationRequest. " +
                $"Set IntegrationEndpoint.SoapAction for system " +
                $"'{system.IntegrationSystemCode}'.");

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

    // ── IIntegrationClient ───────────────────────────────────────────────

    private async Task<IntegrationResponse> ExecuteOnceAsync(
       IntegrationRequest request,
       IntegrationSystem system,
       CancellationToken ct)
    {
        var url = BuildUrl(system, request);

        logger.LogDebug(
            "{Client} → {SoapAction} | {Url} | System={SystemName} [{SystemCode}]",
            GetType().Name, request.SoapAction, url,
            system.SystemName, system.IntegrationSystemCode);

        IRequestChannel? channel = null;
        try
        {
            var factory = factoryManager.GetOrCreate(system, CreateFactory);

            channel = factory.CreateChannel(new EndpointAddress(url));
            channel.Open();

            using var wcfMessage = CreateMessage(request.Payload!, request.SoapAction!);

            // Correct — APM wrapped in TAP, cancellation via .WaitAsync()
            using var reply = await Task.Factory.FromAsync(
                channel.BeginRequest(wcfMessage, null, null),
                channel.EndRequest)
                .WaitAsync(ct);   // .NET 6+ — throws OperationCanceledException if ct fires

            var xml = ReadMessage(reply);

            var fault = SoapFaultParser.TryParse(xml);
            if (fault is not null)
            {
                logger.LogWarning(
                    "{Client} SOAP Fault | Code={Code} Reason={Reason} | " +
                    "System={SystemName} [{SystemCode}]",
                    GetType().Name, fault.Code, fault.Reason,
                    system.SystemName, system.IntegrationSystemCode);

                return IntegrationResponse.Fault(fault, xml);
            }

            SafeClose(channel);
            channel = null;

            logger.LogDebug(
                "{Client} ← success | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);

            return IntegrationResponse.Ok(SoapFaultParser.ExtractBody(xml));
        }
        catch (IntegrationMessagingException)
        {
            channel?.Abort();
            throw;
        }
        catch (CommunicationException ex)
        {
            channel?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogWarning(ex,
                "{Client} CommunicationException (retry eligible) | " +
                "System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;   // ← rethrow — Polly retries, SendAsync wraps after exhaustion
        }
        catch (TimeoutException ex)
        {
            channel?.Abort();
            logger.LogWarning(ex,
                "{Client} Timeout (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;   // ← rethrow — Polly retries
        }
        catch (Exception ex)
        {
            channel?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogError(ex,
                "{Client} unexpected error | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: unexpected error for " +
                $"'{system.SystemName}' ({system.IntegrationSystemCode}): {ex.Message}", ex);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Message CreateMessage(string bodyXml, string soapAction)
    {
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(bodyXml),
                new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });

            return Message.CreateMessage(MessageVersion.Soap11, soapAction, reader);
        }
        catch (XmlException ex)
        {
            throw new IntegrationMessagingException(
                $"SOAP body is not valid XML: {ex.Message}", ex);
        }
    }

    private static string ReadMessage(Message message)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = true
        });
        message.WriteMessage(writer);
        writer.Flush();
        return sb.ToString();
    }

    private static string BuildUrl(IntegrationSystem system, IntegrationRequest request)
    => UrlBuilder.Build(system, request);

    private static void SafeClose(IRequestChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}