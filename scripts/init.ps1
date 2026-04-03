# scripts/init.ps1
# Full first-run initializer for Windows PowerShell.
# Usage: .\scripts\init.ps1 [-Env Development|Production]
#
# Requires: .NET 10 SDK, SQL Server reachable per appsettings.json

param(
    [string]$Env = "Development"
)

$ErrorActionPreference = "Stop"

$Project  = "IntegrationMessaging"
$Startup  = "IntegrationMessaging.Host"
$Migration = "InitialCreate"

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  IntegrationMessaging — First-Run Initializer    ║"
Write-Host "╚══════════════════════════════════════════════════╝"
Write-Host ""

# 1. EF tools
Write-Host "► Installing/updating dotnet-ef tool…"
dotnet tool install --global dotnet-ef 2>$null
if ($LASTEXITCODE -ne 0) { dotnet tool update --global dotnet-ef }

# 2. Restore + build
Write-Host "► Restoring packages…"
dotnet restore
Write-Host "► Building…"
dotnet build --no-restore -c Release

# 3. Create migration if not present
$MigrationsDir = "$Project\Migrations"
if (Test-Path $MigrationsDir) {
    $files = Get-ChildItem -Path $MigrationsDir -Filter "*.cs" -ErrorAction SilentlyContinue
    if ($files.Count -gt 0) {
        Write-Host "► Migrations folder exists — skipping creation."
    } else {
        Write-Host "► Creating migration '$Migration'…"
        dotnet ef migrations add $Migration `
            --project $Project `
            --startup-project $Startup `
            --output-dir Migrations `
            -- --environment $Env
    }
} else {
    Write-Host "► Creating migration '$Migration'…"
    dotnet ef migrations add $Migration `
        --project $Project `
        --startup-project $Startup `
        --output-dir Migrations `
        -- --environment $Env
}

# 4. Add DropDeadLetterQueueFK if upgrading from older version
$existingMigrations = dotnet ef migrations list `
    --project $Project --startup-project $Startup 2>&1
if ($existingMigrations -notlike "*DropDeadLetterQueueFK*") {
    Write-Host "► Adding DropDeadLetterQueueFK migration…"
    dotnet ef migrations add DropDeadLetterQueueFK `
        --project $Project `
        --startup-project $Startup `
        -- --environment $Env
}

# 5. Apply
Write-Host "► Applying migrations…"
dotnet ef database update `
    --project $Project `
    --startup-project $Startup `
    -- --environment $Env

Write-Host ""
Write-Host "✅ Database ready. Starting host…"
Write-Host ""

dotnet run --project $Startup --environment $Env
