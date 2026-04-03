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
using IntegrationMessaging.Services.IntegrationClients.Options;
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
        // ── Options ───────────────────────────────────────────────────
        var optBuilder = services.AddOptions<IntegrationMessagingOptions>()
            .BindConfiguration(IntegrationMessagingOptions.Section);
        if (configure is not null)
            optBuilder.Configure(configure);

        // ── Database ──────────────────────────────────────────────────
        services.AddDbContext<IntegrationDbContext>(opts =>
            opts.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 3);
                sql.CommandTimeout(30);
            }));

        // ── HTTP Clients ──────────────────────────────────────────────
        //services.AddHttpClient(ClientKeys.RestJwt)
        //    .AddStandardResilienceHandler(r =>
        //    {
        //        r.Retry.MaxRetryAttempts = 2;
        //        r.Retry.Delay = TimeSpan.FromMilliseconds(300);
        //    });

        services.AddHttpClient("BDZ_SITE_SCHEDULE", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60); // uploads can be slow
        });

        // ── System-specific HTTP clients ─────────────────────────────────────
        //services.AddHttpClient("PORT_A", c =>
        //{
        //    c.Timeout = TimeSpan.FromSeconds(30);
        //}).AddStandardResilienceHandler();

        //services.AddHttpClient("SHIPSAN", c =>
        //{
        //    c.Timeout = TimeSpan.FromSeconds(15);
        //}).AddStandardResilienceHandler();

        //services.AddHttpClient("PCS")
        //    .ConfigurePrimaryHttpMessageHandler(sp =>
        //    {
        //        //var cert = LoadCertificate(sp); // load from cert store / file
        //        var handler = new HttpClientHandler();
        //        //handler.ClientCertificates.Add(cert);
        //        return handler;
        //    });

        // ── System-specific options ───────────────────────────────────────────
        services.AddOptions<WasteSoapOptions>()
            .BindConfiguration(WasteSoapOptions.Section);

        services.AddHttpClient("IntegrationMessaging.Auth");

        // Encryption
        services.AddOptions<EncryptionOptions>().BindConfiguration(EncryptionOptions.Section);
        services.AddSingleton<IPasswordEncryptor, AesPasswordEncryptor>();

        // Factory manager — Singleton: one cache for the whole process lifetime
        // One line covers ALL typed soap clients — DI resolves TContract automatically
        services.AddSingleton(
            typeof(ISoapChannelFactoryManager<>),
            typeof(SoapChannelFactoryManager<>));

        // ── Cache ─────────────────────────────────────────────────────
        services.AddMemoryCache();

        // ── Core Services ─────────────────────────────────────────────
        services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();
        services.AddScoped<ITokenProvider, JwtTokenProvider>();
        services.AddScoped<IEndpointResolver, EndpointResolver>();
        services.AddScoped<IMessageDispatcher, MessageDispatcher>();
        services.AddScoped<IMessageProcessor, MessageProcessor>();
        services.AddScoped<IDeadLetterService, DeadLetterService>();
        services.AddSingleton<IResiliencePipelineFactory, ResiliencePipelineFactory>();

        // ── REST Client ───────────────────────────────────────────────
        //services.AddKeyedScoped<IIntegrationClient, RestJwtIntegrationClient>(ClientKeys.RestJwt);
        services.AddKeyedSingleton<IIntegrationClient, WasteSoapClient>("WASTE_SOAP");

        services.AddKeyedScoped<IIntegrationClient, BdzSiteScheduleClient>("BDZ_SITE_SCHEDULE");
        services.AddKeyedScoped<IIntegrationMessageHandler, BdzSiteScheduleHandler>(HandlerKeys.BdzSiteSchedule);
        services.AddKeyedScoped<IIntegrationClient, PortAuthorityAClient>("PORT_A");
        services.AddKeyedScoped<IIntegrationClient, PcsClient>("PCS");

        // ── SOAP Clients (same impl, three binding keys) ───────────────
        //services.AddKeyedScoped<IIntegrationClient, SoapWcfIntegrationClient>(ClientKeys.SoapBasic);
        //services.AddKeyedScoped<IIntegrationClient, SoapWcfIntegrationClient>(ClientKeys.SoapBasicHttps);
        //services.AddKeyedScoped<IIntegrationClient, SoapWcfIntegrationClient>(ClientKeys.SoapWs);

        // ── Message Handlers ──────────────────────────────────────────
        services.AddKeyedScoped<IIntegrationMessageHandler, WasteNotificationHandler>(
            HandlerKeys.WasteNotification);
        services.AddKeyedScoped<IIntegrationMessageHandler, WasteRequestHandler>(
            HandlerKeys.WasteRequest);
        services.AddKeyedScoped<IIntegrationMessageHandler, SSNNotificationHandler>(
            HandlerKeys.SSNNotification);
        services.AddKeyedScoped<IIntegrationMessageHandler, SHIPSANNotificationHandler>(
            HandlerKeys.SHIPSANNotification);
        services.AddKeyedScoped<IIntegrationMessageHandler, PCSNotificationHandler>(
            HandlerKeys.PCSNotification);

        // ── Worker + Health Checks ────────────────────────────────────
        services.AddHostedService<IntegrationWorker>();
        services.AddHealthChecks()
            .AddDbContextCheck<IntegrationDbContext>("integration-db")
            .AddCheck<IntegrationHealthCheck>("integration-queue");

        return services;
    }
}
