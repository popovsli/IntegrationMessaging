// Systems/PortAuthorityA/PortAuthorityAClient.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Services.Clients.Base;
using IntegrationMessaging.Services.Resilience;
using IntegrationMessaging.Services.TokenProviders;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace IntegrationMessaging.Services.IntegrationClients;

public sealed class PortAuthorityAClient(
    IHttpClientFactory httpFactory,
    ITokenProvider tokenProvider,
    IResiliencePipelineFactory pipelineFactory,
    ILogger<PortAuthorityAClient> logger)
    : RestIntegrationClientBase(httpFactory, pipelineFactory, logger)
{
    protected override string HttpClientName => "PORT_A";

    protected override async Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        IntegrationSystem system,
        CancellationToken ct)
    {
        var token = await tokenProvider.GetTokenAsync(system, ct);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    protected override IEnumerable<KeyValuePair<string, string>> GetDefaultHeaders(
        IntegrationSystem system)
        => [new("X-Api-Version", "2024-01"), new("X-Source", "PortalSystem")];
}