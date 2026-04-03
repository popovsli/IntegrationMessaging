using IntegrationMessaging.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIntegrationMessaging(
    builder.Configuration.GetConnectionString("IntegrationDb")!,
    opts =>
    {
        opts.BatchSize            = 100;
        opts.PollIntervalSeconds  = 5;
        opts.LockDurationMinutes  = 5;
        opts.EndpointCacheMinutes = 10;
    });



var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/", () => "IntegrationMessaging Host running.");

app.Run();
