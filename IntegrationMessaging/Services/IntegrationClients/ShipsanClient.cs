// Systems/Pcs/PcsClient.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Services.Clients.Base;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services.IntegrationClients;

public sealed class PcsClient(
    IHttpClientFactory httpFactory,
    ILogger<PcsClient> logger)
    : RestIntegrationClientBase(httpFactory, logger)
{
    // Certificate is attached at HttpClient registration time (see DI below)
    protected override string HttpClientName => "PCS";

    protected override Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        IntegrationSystem system,
        CancellationToken ct)
        => Task.CompletedTask; // mTLS is handled by the HttpClientHandler
}