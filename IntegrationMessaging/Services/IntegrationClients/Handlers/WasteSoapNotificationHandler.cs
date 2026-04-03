// Systems/WasteSoap/WasteSoapNotificationHandler.cs
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services;
using IntegrationMessaging.Services.Handlers;
using IntegrationMessaging.Services.IntegrationClients.DTOs;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class WasteSoapNotificationHandler(
    IEndpointResolver endpointResolver) : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.WasteNotification;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        // Deserialize JSON from queue → plain DTO
        // No WCF types, no proxy classes, no Func<T> — just a DTO
        var dto = JsonSerializer.Deserialize<WasteNotificationDto>(
            q.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IntegrationMessagingException(
                "WasteNotification payload could not be deserialized.");

        return new IntegrationRequest
        {
            EndpointPath = resolution.ResolvedPath,
            TypedPayload = dto, // ← handler is done here
            Context = context   // ← forwarded to client
        };
    }
}