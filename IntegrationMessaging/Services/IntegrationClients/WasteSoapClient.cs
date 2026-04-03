// Systems/WasteSoap/WasteSoapClient.cs
using Azure;
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients.Soap;
using IntegrationMessaging.Services.Handlers;
using IntegrationMessaging.Services.IntegrationClients.Contracts;
using IntegrationMessaging.Services.IntegrationClients.DTOs;
using Microsoft.Extensions.Logging;
using System.ServiceModel;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients;

public sealed class WasteSoapClient(
    ISoapChannelFactoryManager<IWasteService> factoryManager,  // ← typed correctly
    ILogger<WasteSoapClient> logger)
    : TypedSoapClientBase<IWasteService>(factoryManager, logger)
{
    // ── Binding: entirely in code ─────────────────────────────────────────

    protected override ChannelFactory<IWasteService> CreateFactory(
        string username, string password)
    {
        var factory = new ChannelFactory<IWasteService>(
            new BasicHttpsBinding
            {
                Security =
                {
                    Mode      = BasicHttpsSecurityMode.Transport,
                    Transport = { ClientCredentialType = HttpClientCredentialType.Basic }
                },
                MaxReceivedMessageSize = 10 * 1024 * 1024,
                SendTimeout = TimeSpan.FromSeconds(30),
                ReceiveTimeout = TimeSpan.FromSeconds(30)
            },
            new EndpointAddress("http://placeholder"));

        if (!string.IsNullOrWhiteSpace(username))
        {
            factory.Credentials.UserName.UserName = username;
            factory.Credentials.UserName.Password = password;
        }

        return factory;
    }

    // ── DTO → WCF contract mapping ────────────────────────────────────────
    protected override async Task<string?> InvokeContractAsync(
        IWasteService channel,
        IntegrationRequest request,
        SendContext context,
        CancellationToken ct)
    {
        var operation = context.QueueMessage.MessageOperation;

        return context.QueueMessage.MessageTypeName switch
        {
            HandlerKeys.WasteNotification =>
                await InvokeWasteNotificationAsync(channel, request, context.QueueMessage.MessageOperation),

            // Future message type for same system — add a case here only
            // case HandlerKeys.WasteManifest:
            //     await InvokeWasteManifestAsync(channel, request, operation);
            //     break;

            _ => throw new IntegrationMessagingException(
                    $"{nameof(WasteSoapClient)} does not support " +
                    $"MessageTypeName='{context.QueueMessage.MessageTypeName}'.")
        };
    }

    private static async Task<string?> InvokeWasteNotificationAsync(
        IWasteService channel,
        IntegrationRequest request,
        MessageOperation operation)
    {
        var dto = request.TypedPayload as WasteNotificationDto
            ?? throw new IntegrationMessagingException(
                $"{nameof(WasteSoapClient)} expected a {nameof(WasteNotificationDto)} " +
                "in request.TypedPayload.");

        switch (operation)
        {
            case MessageOperation.Create:
            case MessageOperation.Update:
                var response = await channel.SubmitWasteNotificationAsync(
                    new SubmitWasteNotificationRequest
                    {
                        VesselCallId = dto.VesselCallId,
                        WasteTypeCode = dto.WasteTypeCode,
                        QuantityM3 = dto.QuantityM3,
                        PortCode = dto.PortCode
                    });

                // Serialize the meaningful response — stored in IntegrationMessage.ResponsePayload
                return JsonSerializer.Serialize(new
                {
                    response.ConfirmationRef,
                    response.AcceptedAtUtc
                });

            case MessageOperation.Delete:
                await channel.CancelWasteNotificationAsync(
                    new CancelWasteNotificationRequest
                    {
                        VesselCallId = dto.VesselCallId,
                        CancelledAtUtc = DateTime.UtcNow
                    });
                return null;  // ← explicitly void

            default:
                throw new IntegrationMessagingException(
                    $"Unsupported operation '{operation}' for {HandlerKeys.WasteNotification}.");
        }
    }
}

