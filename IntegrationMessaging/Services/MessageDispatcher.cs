using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients;
using IntegrationMessaging.Services.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services;

public sealed class MessageDispatcher(
    IServiceProvider services,
    ILogger<MessageDispatcher> logger) : IMessageDispatcher
{
    //public async Task<IntegrationResponse> DispatchAsync(
    //    SendContext context, CancellationToken ct = default)
    //{
    //    var messageTypeName = context.QueueMessage.MessageTypeName;
    //    var clientType = context.System.ClientType;

    //    var handler = services.GetKeyedService<IIntegrationMessageHandler>(messageTypeName)
    //        ?? throw new IntegrationMessagingException(
    //            $"No handler for MessageTypeName='{messageTypeName}'. " +
    //            $"Register via AddKeyedScoped<IIntegrationMessageHandler,T>(\"{messageTypeName}\").");

    //    var client = services.GetKeyedService<IIntegrationClient>(clientType)
    //        ?? throw new IntegrationMessagingException(
    //            $"No client for ClientType='{clientType}'. " +
    //            $"Register via AddKeyedScoped<IIntegrationClient, T>(\"{clientType}\").");

    //    logger.LogDebug(
    //        "Dispatching {MessageTypeName} via {ClientType} | EntityId={EntityId} System={System}",
    //        messageTypeName, clientType,
    //        context.QueueMessage.EntityId, context.QueueMessage.IntegrationSystemCode);

    //    var request = await handler.BuildRequestAsync(context, ct);
    //    var response = await client.SendAsync(request, context.System, ct);
    //    context.Response = response;
    //    return response;
    //}

    public async Task<IntegrationResponse> DispatchAsync(
    SendContext context, CancellationToken ct = default)
    {
        var messageTypeName = context.QueueMessage.MessageTypeName;
        var systemCode = context.QueueMessage.IntegrationSystemCode;

        var handler = services.GetKeyedService<IIntegrationMessageHandler>(messageTypeName)
            ?? throw new IntegrationMessagingException(
                $"No handler registered for MessageTypeName='{messageTypeName}'.");

        // ← NEW: resolve by system code, not client type
        var client = services.GetKeyedService<IIntegrationClient>(systemCode)
            ?? throw new IntegrationMessagingException(
                $"No client registered for IntegrationSystemCode='{systemCode}'. " +
                $"Create a class implementing IIntegrationClient and register it with key '{systemCode}'.");

        var request = await handler.BuildRequestAsync(context, ct);
        var response = await client.SendAsync(request, context.System, ct);
        context.Response = response;
        return response;
    }
}
