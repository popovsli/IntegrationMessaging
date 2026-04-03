using IntegrationMessaging.Data;
using IntegrationMessaging.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "INTEGRATION_")     // INTEGRATION_ConnectionStrings__IntegrationMessaging=...
    .AddUserSecrets<Program>(optional: true);            // dev-only: dotnet user-secrets set ...

var connectionString = builder.Configuration
    .GetConnectionString("IntegrationMessaging")
    ?? throw new InvalidOperationException(
        "Missing required connection string 'IntegrationMessaging'. " +
        "Add it to appsettings.json or set env var " +
        "INTEGRATION_ConnectionStrings__IntegrationMessaging.");

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddIntegrationMessaging(connectionString);

// Database initializer — scoped so it receives a fresh DbContext
builder.Services.AddScoped<DatabaseInitializer>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Database: migrate + seed (runs before worker starts processing) ───────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new
{
    name = "IntegrationMessaging.Host",
    environment = app.Environment.EnvironmentName,
    status = "running",
    utc = DateTime.UtcNow
}));

app.MapHealthChecks("/health");
app.MapControllers();

await app.RunAsync();
