#!/usr/bin/env bash
# scripts/init.sh
# Full first-run initializer: installs EF tools, creates the initial migration,
# applies it, then starts the host.
# Usage: bash scripts/init.sh [--env Development|Production]
#
# Requires: .NET 10 SDK, SQL Server reachable per appsettings.json

set -euo pipefail

ENV=${1:-Development}
PROJECT="IntegrationMessaging"
STARTUP="IntegrationMessaging.Host"
MIGRATION="InitialCreate"

echo ""
echo "╔══════════════════════════════════════════════════╗"
echo "║  IntegrationMessaging — First-Run Initializer    ║"
echo "╚══════════════════════════════════════════════════╝"
echo ""

# 1. Install / update EF Core tools
echo "► Installing/updating dotnet-ef tool…"
dotnet tool install --global dotnet-ef 2>/dev/null \
  || dotnet tool update --global dotnet-ef

# 2. Restore packages
echo "► Restoring NuGet packages…"
dotnet restore

# 3. Build
echo "► Building solution…"
dotnet build --no-restore -c Release

# 4. Create initial migration if it doesn't exist
MIGRATIONS_DIR="$PROJECT/Migrations"
if [ -d "$MIGRATIONS_DIR" ] && ls "$MIGRATIONS_DIR"/*.cs 1>/dev/null 2>&1; then
  echo "► Migrations folder already exists — skipping migration creation."
else
  echo "► Creating migration '$MIGRATION'…"
  dotnet ef migrations add "$MIGRATION" \
    --project "$PROJECT" \
    --startup-project "$STARTUP" \
    --output-dir Migrations \
    -- --environment "$ENV"
fi

# 5. Check for pending migrations (DropDeadLetterQueueFK if upgrading)
echo "► Checking for additional migrations…"
if grep -r "DropDeadLetterQueueFK" "$MIGRATIONS_DIR" 1>/dev/null 2>&1; then
  echo "   DropDeadLetterQueueFK migration already present."
else
  echo "   Adding DropDeadLetterQueueFK migration…"
  dotnet ef migrations add DropDeadLetterQueueFK \
    --project "$PROJECT" \
    --startup-project "$STARTUP" \
    -- --environment "$ENV"
fi

# 6. Apply migrations
echo "► Applying migrations to database…"
dotnet ef database update \
  --project "$PROJECT" \
  --startup-project "$STARTUP" \
  -- --environment "$ENV"

echo ""
echo "✅ Database ready. Starting host…"
echo ""

dotnet run --project "$STARTUP" --environment "$ENV"
