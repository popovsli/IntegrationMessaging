// Services/Clients/Base/SoapIntegrationClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients.Soap;
using Microsoft.Extensions.Logging;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Xml;

namespace IntegrationMessaging.Services.Clients.Base;

public abstract class SoapIntegrationClientBase(
    ISoapChannelFactoryManager<IRequestChannel> factoryManager,
    ILogger logger) : IIntegrationClient
{
    // ── Subclass configures the WCF binding entirely in code ─────────────

    /// <summary>
    /// Build a fully configured ChannelFactory — binding + credentials.
    /// Called only when no cached factory exists or config changed in DB.
    /// </summary>
    protected abstract ChannelFactory<IRequestChannel> CreateFactory(
        string username, string password);

    // ── IIntegrationClient ───────────────────────────────────────────────

    public async Task<IntegrationResponse> SendAsync(
        IntegrationRequest request,
        IntegrationSystem system,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SoapAction))
            throw new IntegrationMessagingException(
                $"{GetType().Name} requires a SoapAction on the IntegrationRequest.");

        var url = BuildUrl(system, request);

        logger.LogDebug("{Client} → {SoapAction} | {Url}",
            GetType().Name, request.SoapAction, url);

        IRequestChannel? channel = null;
        try
        {
            var factory = factoryManager.GetOrCreate(system, CreateFactory);

            channel = factory.CreateChannel(new EndpointAddress(url));
            channel.Open();

            using var wcfMessage = CreateMessage(request.Payload, request.SoapAction);
            using var reply = await Task.Factory.FromAsync(
                channel.BeginRequest(wcfMessage, null, null),
                channel.EndRequest);

            var xml = ReadMessage(reply);

            logger.LogDebug("{Client} ← Faulted={IsFault}", GetType().Name, reply.IsFault);


            var fault = SoapFaultParser.TryParse(xml);

            if (fault is not null)
            {
                logger.LogWarning("{Client} SOAP Fault | Code={Code} Reason={Reason}",
                    GetType().Name, fault.Code, fault.Reason);

                return IntegrationResponse.Fault(fault, xml);
            }

            SafeClose(channel);
            channel = null;

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
            // Factory may be in faulted state — discard it so next call rebuilds
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogError(ex, "{Client} CommunicationException", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF communication failed — {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            channel?.Abort();
            logger.LogError(ex, "{Client} Timeout", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: WCF call timed out — {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            channel?.Abort();
            factoryManager.Invalidate(system.IntegrationSystemCode);
            logger.LogError(ex, "{Client} unexpected error", GetType().Name);
            throw new IntegrationMessagingException(
                $"{GetType().Name}: unexpected error — {ex.Message}", ex);
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
    {
        var path = string.IsNullOrWhiteSpace(request.EndpointPath)
            ? system.EndpointPath
            : request.EndpointPath;
        return $"{system.BaseAddress.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static void SafeClose(IRequestChannel channel)
    {
        try { channel.Close(); }
        catch { channel.Abort(); }
    }
}