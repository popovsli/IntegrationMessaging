// Services/Clients/Base/RestIntegrationClientBase.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace IntegrationMessaging.Services.Clients.Base;

public abstract class RestIntegrationClientBase(
    IHttpClientFactory httpFactory,
    ILogger logger) : IIntegrationClient
{
    public async Task<IntegrationResponse> SendAsync(
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

        // Content built separately — client reads Metadata if it needs it
        if (!string.IsNullOrWhiteSpace(request.Payload))
            httpRequest.Content = BuildContent(request, system);  // ← virtual hook

        logger.LogDebug("{Client} → {Method} {Url}", GetType().Name, request.HttpMethod, url);

        using var httpResponse = await client.SendAsync(httpRequest, ct);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

        logger.LogDebug("{Client} ← HTTP {Code}", GetType().Name, (int)httpResponse.StatusCode);

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
    {
        var path = string.IsNullOrWhiteSpace(request.EndpointPath)
            ? system.EndpointPath
            : request.EndpointPath;
        return $"{system.BaseAddress.TrimEnd('/')}/{path.TrimStart('/')}";
    }
}