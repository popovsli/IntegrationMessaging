// Systems/BdzSiteSchedule/BdzSiteScheduleClient.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients.Base;
using IntegrationMessaging.Services.Resilience;
using Microsoft.Extensions.Logging;
using System.Text;

namespace IntegrationMessaging.Services.IntegrationClients;

public sealed class BdzSiteScheduleClient(
    IHttpClientFactory httpFactory,
    IResiliencePipelineFactory pipelineFactory,
    ILogger<BdzSiteScheduleClient> logger)
    : RestIntegrationClientBase(httpFactory, pipelineFactory, logger)
{
    protected override string HttpClientName => "BDZ_SITE_SCHEDULE";

    // No token auth — this system uses anonymous multipart upload
    protected override Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        IntegrationSystem system,
        CancellationToken ct)
        => Task.CompletedTask;

    // ── The key override: build the exact multipart message ──────────────
    protected override HttpContent BuildContent(
        IntegrationRequest request,
        IntegrationSystem system)
    {
        var encoding = Encoding.GetEncoding(1251);
        var fileName = request.Metadata.GetValueOrDefault("FileName", "upload.xml");

        var xmlPart = new StringContent(request.Payload, encoding, "application/xml");
        var boundary = "--------------------------" + DateTime.Now.Ticks;
        var multipart = new MultipartFormDataContent(boundary);

        multipart.Headers.ContentType!.Parameters
            .Single(p => p.Name == "boundary").Value
            = boundary.Replace("\"", "");

        multipart.Add(xmlPart, name: "upload", fileName: fileName);
        return multipart;
    }
}