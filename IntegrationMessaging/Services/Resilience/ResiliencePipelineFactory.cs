// Services/Resilience/ResiliencePipelineFactory.cs
// FIX #9 (carried forward): pipelines cached per system.
// FIX NEW-C: cached by (SystemCode + config fingerprint) — if ClientRetryCount
//            or ClientTimeoutSeconds changes in the DB, the next call rebuilds
//            the pipeline automatically instead of silently using stale settings.
//            Pattern mirrors SoapChannelFactoryManager's fingerprint approach.

using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.ServiceModel;
using System.Text;

namespace IntegrationMessaging.Services.Resilience;

public sealed class ResiliencePipelineFactory(
    ILogger<ResiliencePipelineFactory> logger) : IResiliencePipelineFactory
{
    private sealed record CachedPipeline(
        ResiliencePipeline<IntegrationResponse> Pipeline,
        string Fingerprint);

    private readonly ConcurrentDictionary<string, CachedPipeline>
        _rest = new();
    private readonly ConcurrentDictionary<string, CachedPipeline>
        _soap = new();

    public ResiliencePipeline<IntegrationResponse> CreateRestPipeline(
        IntegrationSystem system)
    {
        var fp = Fingerprint(system);
        if (_rest.TryGetValue(system.IntegrationSystemCode, out var cached)
            && cached.Fingerprint == fp)
            return cached.Pipeline;

        var pipeline = Build(system,
            new PredicateBuilder<IntegrationResponse>()
                .Handle<HttpRequestException>()
                .HandleResult(r =>
                    r.HttpStatusCode >= (int)HttpStatusCode.InternalServerError));

        _rest[system.IntegrationSystemCode] =
            new CachedPipeline(pipeline, fp);

        return pipeline;
    }

    public ResiliencePipeline<IntegrationResponse> CreateSoapPipeline(
        IntegrationSystem system)
    {
        var fp = Fingerprint(system);
        if (_soap.TryGetValue(system.IntegrationSystemCode, out var cached)
            && cached.Fingerprint == fp)
            return cached.Pipeline;

        var pipeline = Build(system,
            new PredicateBuilder<IntegrationResponse>()
                .Handle<CommunicationException>()
                .Handle<TimeoutException>());

        _soap[system.IntegrationSystemCode] =
            new CachedPipeline(pipeline, fp);

        return pipeline;
    }

    // ── Builder ───────────────────────────────────────────────────────────

    private ResiliencePipeline<IntegrationResponse> Build(
        IntegrationSystem system,
        PredicateBuilder<IntegrationResponse> shouldHandle)
    {
        var timeoutSeconds = Math.Max(1, system.ClientTimeoutSeconds);

        return new ResiliencePipelineBuilder<IntegrationResponse>()
            // Outer: per-attempt timeout
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            })
            // Inner: retry with exponential back-off
            .AddRetry(new RetryStrategyOptions<IntegrationResponse>
            {
                ShouldHandle = shouldHandle,
                MaxRetryAttempts = system.ClientRetryCount,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Retry {Attempt}/{Max} after {Delay:ss\\s} | " +
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

    // Covers every DB column that affects pipeline behaviour
    private static string Fingerprint(IntegrationSystem s)
    {
        var raw = $"{s.ClientRetryCount}|{s.ClientTimeoutSeconds}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
