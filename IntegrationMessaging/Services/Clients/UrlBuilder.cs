// Services/Clients/UrlBuilder.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services.Clients;

internal static class UrlBuilder
{
    /// <summary>
    /// Builds the full request URL from BaseAddress + EndpointPath.
    /// EndpointPath must always come from IEndpointResolver (IntegrationEndpoint table).
    /// A missing EndpointPath means a misconfigured IntegrationEndpoint row — fail fast.
    /// </summary>
    internal static string Build(IntegrationSystem system, IntegrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EndpointPath))
            throw new IntegrationMessagingException(
                $"EndpointPath is required but was not set on IntegrationRequest. " +
                $"Ensure an IntegrationEndpoint row exists for " +
                $"system='{system.IntegrationSystemCode}' " +
                $"and MessageTypeName='{request.Context?.QueueMessage.MessageTypeName}'.");

        return $"{system.BaseAddress.TrimEnd('/')}/{request.EndpointPath.TrimStart('/')}";
    }
}