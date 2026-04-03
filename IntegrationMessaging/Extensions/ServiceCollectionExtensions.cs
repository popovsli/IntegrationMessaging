using IntegrationMessaging.Configuration;
using IntegrationMessaging.Data;
using IntegrationMessaging.HealthChecks;
using IntegrationMessaging.Services;
using IntegrationMessaging.Services.CircuitBreaker;
using IntegrationMessaging.Services.Clients;
using IntegrationMessaging.Services.Clients.Soap;
using IntegrationMessaging.Services.Handlers;
using IntegrationMessaging.Services.IntegrationClients;
using IntegrationMessaging.Services.IntegrationClients.Handlers;
using IntegrationMessaging.Services.Resilience;
using IntegrationMessaging.Services.Security;
using IntegrationMessaging.Services.TokenProviders;
using IntegrationMessaging.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationMessaging.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the complete IntegrationMessaging library.
    /// Call after this to register any additional IIntegrationClient or IIntegrationMessageHandler.
    /// </summary>
    public static IServiceCollection AddIntegrationMessaging(
        this IServiceCollection services,
        string connectionString,
        Action<IntegrationMessagingOptions>? configure = null)
    {
        // ── System-specific options ───────────────────────────────────────────
        services.AddOptions<IntegrationMessagingOptions>()
            .BindConfiguration(IntegrationMessagingOptions.Section)
            .Configure(o => configure?.Invoke(o))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EncryptionOptions>()
            .BindConfiguration(EncryptionOptions.Section)
            .ValidateOnStart();

        // ── Database ──────────────────────────────────────────────────
        services.AddDbContext<IntegrationDbContext>(opts =>
            opts.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 3);
                sql.CommandTimeout(30);
            }));

        // ── HTTP Clients ──────────────────────────────────────────────────
        services.AddHttpClient("BDZ_SITE_SCHEDULE",
            c => c.Timeout = TimeSpan.FromSeconds(60));

        services.AddHttpClient("PORT_A",
            c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddStandardResilienceHandler();

        services.AddHttpClient("PCS",
            c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddStandardResilienceHandler();

        services.AddHttpClient("SHIPSAN",
            c => c.Timeout = TimeSpan.FromSeconds(15))
            .AddStandardResilienceHandler();

        // FIX NEW-E: auth client gets resilience so token-endpoint blips are retried
        services.AddHttpClient("IntegrationMessaging.Auth")
            .AddStandardResilienceHandler(r =>
            {
                r.Retry.MaxRetryAttempts = 2;
                r.Retry.Delay = TimeSpan.FromMilliseconds(500);
            });


        // ── Cache ─────────────────────────────────────────────────────
        services.AddMemoryCache();

        // ── Infrastructure (Singleton — process-lifetime caches) ─────────
        services.AddSingleton<IPasswordEncryptor, AesPasswordEncryptor>();
        services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        services.AddSingleton<IResiliencePipelineFactory, ResiliencePipelineFactory>();
        // Factory manager — Singleton: one cache for the whole process lifetime
        // One line covers ALL typed soap clients — DI resolves TContract automatically
        services.AddSingleton(
            typeof(ISoapChannelFactoryManager<>),
            typeof(SoapChannelFactoryManager<>));

        // ── Core Services ─────────────────────────────────────────────
        services.AddScoped<IMessageProcessor, MessageProcessor>();
        services.AddScoped<IMessageDispatcher, MessageDispatcher>();
        services.AddScoped<IEndpointResolver, EndpointResolver>();
        services.AddScoped<ITokenProvider, JwtTokenProvider>();
        services.AddScoped<IDeadLetterService, DeadLetterService>();

        // ── REST Client ───────────────────────────────────────────────
        services.AddKeyedScoped<IIntegrationClient, WasteSoapClient>("WASTE_SOAP");
        services.AddKeyedScoped<IIntegrationClient, BdzSiteScheduleClient>("BDZ_SITE_SCHEDULE");
        services.AddKeyedScoped<IIntegrationClient, PortAuthorityAClient>("PORT_A");

        // ── Message Handlers ──────────────────────────────────────────
        services.AddKeyedScoped<IIntegrationMessageHandler, WasteRequestHandler>(HandlerKeys.WasteRequest);
        services.AddKeyedScoped<IIntegrationMessageHandler, BdzSiteScheduleHandler>(HandlerKeys.BdzSiteSchedule);

        // ── Worker + Health Checks ────────────────────────────────────
        services.AddHostedService<IntegrationWorker>();
        services.AddHealthChecks()
            .AddDbContextCheck<IntegrationDbContext>("integration-db")
            .AddCheck<IntegrationHealthCheck>("integration-queue");

        return services;
    }
}