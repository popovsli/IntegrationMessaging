// Services/Clients/Base/RestIntegrationClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Resilience;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace IntegrationMessaging.Services.Clients.Base;

public abstract class RestIntegrationClientBase(
    IHttpClientFactory httpFactory,
    IResiliencePipelineFactory pipelineFactory,
    ILogger logger) : IIntegrationClient
{

    public async Task<IntegrationResponse> SendAsync(
       IntegrationRequest request,
       IntegrationSystem system,
       CancellationToken ct = default)
    {
        var pipeline = pipelineFactory.CreateRestPipeline(system);
        return await pipeline.ExecuteAsync(
            async token => await ExecuteOnceAsync(request, system, token), ct);
    }

    private async Task<IntegrationResponse> ExecuteOnceAsync(
    IntegrationRequest request,
    IntegrationSystem system,
    CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        var url = BuildUrl(system, request);

        using var httpRequest = new HttpRequestMessage(
            new HttpMethod(request.HttpMethod), url);

        await ApplyAuthenticationAsync(httpRequest, system, ct);

        foreach (var (key, value) in GetDefaultHeaders(system))
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        // RestIntegrationClientBase — clean, no Where() needed
        foreach (var (key, value) in request.AdditionalHeaders)
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        // TypedPayload takes priority; fall back to pre-serialized Payload string
        if (request.TypedPayload is not null)
            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(request.TypedPayload),
                Encoding.UTF8, ContentType);
        else if (!string.IsNullOrWhiteSpace(request.Payload))
            httpRequest.Content = BuildContent(request, system);
        // else: intentional no-body request (GET, DELETE)

        logger.LogDebug("{Client} → {Method} {Url} | System={SystemName}", GetType().Name, request.HttpMethod, url, system.SystemName);

        using var httpResponse = await client.SendAsync(httpRequest, ct);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

        logger.LogDebug("{Client} ← HTTP {Code} | System={SystemCode}",
           GetType().Name, (int)httpResponse.StatusCode, system.IntegrationSystemCode);

        // RestIntegrationClientBase
        return httpResponse.IsSuccessStatusCode
            ? IntegrationResponse.Ok(responseBody)
            : IntegrationResponse.RestError((int)httpResponse.StatusCode, responseBody);
    }

    // Default: simple StringContent — overridden by BdzSiteScheduleClient
    protected virtual HttpContent BuildContent(
        IntegrationRequest request,
        IntegrationSystem system)
        => new StringContent(request.Payload, Encoding.UTF8, ContentType);

    // ── Overridable hooks ──────────────────────────────────────────────
    protected virtual string HttpClientName => "IntegrationMessaging.Default";
    protected virtual string ContentType => "application/json";

    /// <summary>Apply Bearer token, API key, certificate, etc.</summary>
    protected abstract Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        IntegrationSystem system,
        CancellationToken ct);

    /// <summary>System-level static headers (e.g. X-Api-Version).</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> GetDefaultHeaders(
        IntegrationSystem system)
        => [];

    private static string BuildUrl(IntegrationSystem system, IntegrationRequest request)
        => UrlBuilder.Build(system, request);
}