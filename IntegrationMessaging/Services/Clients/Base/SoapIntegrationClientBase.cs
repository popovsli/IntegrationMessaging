// Services/Clients/Base/SoapIntegrationClientBase.cs
// FIX NEW-F: XmlReaderSettings and XmlWriterSettings promoted to static
//            readonly — avoids per-call heap allocation on every SOAP message.

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
    // FIX NEW-F: static readonly — allocated once per AppDomain
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        DtdProcessing = DtdProcessing.Prohibit
    };

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = Encoding.UTF8,
        OmitXmlDeclaration = true
    };

    protected abstract ChannelFactory<IRequestChannel> CreateFactory(
        string username, string password);

    // ── Public entry point ────────────────────────────────────────────────

    public async Task<IntegrationResponse> SendAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SoapAction))
            throw new IntegrationMessagingException(
                $"{GetType().Name} requires SoapAction on IntegrationRequest. " +
                $"Set IntegrationEndpoint.SoapAction for " +
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

    // ── Single attempt ────────────────────────────────────────────────────

    private async Task<IntegrationResponse> ExecuteOnceAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct)
    {
        var url = UrlBuilder.Build(system, request);

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

            using var wcfMsg = CreateMessage(request.Payload!, request.SoapAction!);

            using var reply = await Task.Factory
                .FromAsync(
                    channel.BeginRequest(wcfMsg, null, null),
                    channel.EndRequest)
                .WaitAsync(ct);

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
        catch (IntegrationMessagingException) { channel?.Abort(); throw; }
        catch (CommunicationException ex)
        {
            channel?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogWarning(ex,
                "{Client} CommunicationException (retry eligible) | " +
                "System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;
        }
        catch (TimeoutException ex)
        {
            channel?.Abort();
            logger.LogWarning(ex,
                "{Client} Timeout (retry eligible) | System={SystemName} [{SystemCode}]",
                GetType().Name, system.SystemName, system.IntegrationSystemCode);
            throw;
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

    // ── Helpers (use static settings) ────────────────────────────────────

    private static Message CreateMessage(string bodyXml, string soapAction)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(bodyXml), ReaderSettings);
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
        using var writer = XmlWriter.Create(sb, WriterSettings);
        message.WriteMessage(writer);
        writer.Flush();
        return sb.ToString();
    }

    private static void SafeClose(IRequestChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}
