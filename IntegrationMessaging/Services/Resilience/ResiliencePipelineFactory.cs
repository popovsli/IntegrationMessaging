// Services/Resilience/ResiliencePipelineFactory.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Net;
using System.ServiceModel;

namespace IntegrationMessaging.Services.Resilience;

public sealed class ResiliencePipelineFactory(
    ILogger<ResiliencePipelineFactory> logger) : IResiliencePipelineFactory
{
    public ResiliencePipeline<IntegrationResponse> CreateRestPipeline(
        IntegrationSystem system) =>
        BuildPipeline(
            system,
            new PredicateBuilder<IntegrationResponse>()
                .Handle<HttpRequestException>()
                .HandleResult(r =>
                    r.HttpStatusCode >= (int)HttpStatusCode.InternalServerError));

    public ResiliencePipeline<IntegrationResponse> CreateSoapPipeline(
        IntegrationSystem system) =>
        BuildPipeline(
            system,
            new PredicateBuilder<IntegrationResponse>()
                .Handle<CommunicationException>()   // transient WCF transport failure
                .Handle<TimeoutException>());        // endpoint slow or overloaded
                                                     // FaultException excluded — business rejection

    // ── Shared builder — same retry shape for all protocols ──────────────

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
                UseJitter = true,   // spread retries across concurrent workers

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