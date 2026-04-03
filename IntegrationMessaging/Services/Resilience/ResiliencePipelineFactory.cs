// Services/Resilience/ResiliencePipelineFactory.cs
// FIX #9: Pipelines are cached per IntegrationSystemCode instead of being
//          rebuilt on every call.  Building a ResiliencePipeline allocates
//          several internal Polly objects — reusing them is the intended pattern.

using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Collections.Concurrent;
using System.Net;
using System.ServiceModel;

namespace IntegrationMessaging.Services.Resilience;

public sealed class ResiliencePipelineFactory(
    ILogger<ResiliencePipelineFactory> logger) : IResiliencePipelineFactory
{
    // FIX #9: cached per system code — pipelines are designed for reuse
    private readonly ConcurrentDictionary<string, ResiliencePipeline<IntegrationResponse>>
        _restPipelines = new();
    private readonly ConcurrentDictionary<string, ResiliencePipeline<IntegrationResponse>>
        _soapPipelines = new();

    public ResiliencePipeline<IntegrationResponse> CreateRestPipeline(
        IntegrationSystem system) =>
        _restPipelines.GetOrAdd(system.IntegrationSystemCode,
            _ => BuildPipeline(system,
                new PredicateBuilder<IntegrationResponse>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r =>
                        r.HttpStatusCode >= (int)HttpStatusCode.InternalServerError)));

    public ResiliencePipeline<IntegrationResponse> CreateSoapPipeline(
        IntegrationSystem system) =>
        _soapPipelines.GetOrAdd(system.IntegrationSystemCode,
            _ => BuildPipeline(system,
                new PredicateBuilder<IntegrationResponse>()
                    .Handle<CommunicationException>()   // transient WCF transport failure
                    .Handle<TimeoutException>()));       // FaultException excluded — business rejection

    // ── Shared builder ────────────────────────────────────────────────────

    private ResiliencePipeline<IntegrationResponse> BuildPipeline(
        IntegrationSystem system,
        PredicateBuilder<IntegrationResponse> shouldHandle) =>
        new ResiliencePipelineBuilder<IntegrationResponse>()
            .AddRetry(new RetryStrategyOptions<IntegrationResponse>
            {
                ShouldHandle = shouldHandle,
                MaxRetryAttempts = system.ClientRetryCount,

                // Exponential backoff: 2s → 4s → 8s…
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,   // avoids retry storms across concurrent workers

                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retry {Attempt}/{Max} after {Delay:ss}s | " +
                        "System={SystemName} [{SystemCode}] | Reason={Reason}",
                        args.AttemptNumber + 1,
                        system.ClientRetryCount,
                        args.RetryDelay,
                        system.SystemName,
                        system.IntegrationSystemCode,
                        args.Outcome.Exception?.Message
                            ?? $"HTTP {args.Outcome.Result?.HttpStatusCode}");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
}
