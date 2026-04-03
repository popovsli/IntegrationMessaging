# IntegrationMessaging — First-Run Guide

Everything needed to take the application from zero to running — on a developer machine, in CI, or inside Docker.

---

## Prerequisites

| Requirement | Min version | Check |
|---|---|---|
| .NET SDK | **10.0** | `dotnet --version` |
| SQL Server | 2019+ (or Azure SQL / LocalDB) | `sqlcmd -?` |
| `dotnet-ef` CLI | latest | `dotnet ef --version` |
| PowerShell (Windows) | 7+ | `pwsh --version` |

> **Docker shortcut:** if you have Docker, jump straight to [Docker Compose](#docker-compose) — no local SQL Server needed.

---

## Option A — One Command

The `scripts/` directory contains fully automated first-run scripts.  
They install `dotnet-ef`, restore packages, build, create migrations, apply them, then start the host.

```bash
# Linux / macOS / WSL
bash scripts/init.sh

# Windows PowerShell
.\scripts\init.ps1
```

Both accept an optional environment flag:

```bash
bash scripts/init.sh --env Production
.\scripts\init.ps1 -Env Production
```

---

## Option B — Manual Steps

### Step 1 — Set the Encryption Key

The AES-256 key is **required** at startup and must never be committed to source control.

```bash
cd IntegrationMessaging.Host

# Development: dotnet user-secrets (ignored by git)
dotnet user-secrets set "Encryption:Key" "your-32-char-minimum-secret-key!!"
```

For production, use an environment variable or Azure Key Vault:

```bash
export INTEGRATION_Encryption__Key="your-32-char-minimum-secret-key!!"
```

> System-level credentials (passwords for PORT_A, BDZ_SITE_SCHEDULE, SHIPSAN, etc.)
> are stored **encrypted** in the `IntegrationSystem` table and set after first run —
> see [Setting Credentials](#setting-credentials-after-first-run).

### Step 2 — Verify the Connection String

`appsettings.Development.json` defaults to `localhost,1433` with the `sa` account:

```json
{
  "ConnectionStrings": {
    "IntegrationMessaging": "Server=localhost,1433;Database=IntegrationMessagingDb;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True;MultipleActiveResultSets=True;App=IntegrationMessaging"
  }
}
```

Override without editing the file:

```bash
export INTEGRATION_ConnectionStrings__IntegrationMessaging="Server=...;Database=IntegrationMessagingDb;..."
```

### Step 3 — Install the EF Tool

```bash
dotnet tool install --global dotnet-ef
# Already installed? Update instead:
dotnet tool update --global dotnet-ef

dotnet ef --version   # Expected: 10.x.x
```

### Step 4 — Create the Initial Migration

Run from the **solution root**:

```bash
dotnet ef migrations add InitialCreate \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host \
  --output-dir Migrations
```

This generates `IntegrationMessaging/Migrations/` with the full schema for all five tables.

#### Upgrading from a pre-v2 deployment?

If you have a live database that was created before the dead-letter FK fix, add a second migration:

```bash
dotnet ef migrations add DropDeadLetterQueueFK \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host
```

The `init.sh` / `init.ps1` scripts add this migration automatically.

### Step 5 — Apply Migrations

**Option 1 — CLI** (explicit, preferred for production deployments):

```bash
dotnet ef database update \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host \
  --environment Development
```

**Option 2 — Auto-apply on startup** (default):  
`DatabaseInitializer` calls `MigrateAsync()` automatically before the worker starts.  
It retries the DB connection up to **10 times with 2-second delays** — safe for Docker.

### Step 6 — Run

```bash
dotnet run --project IntegrationMessaging.Host --environment Development
```

Expected startup output:

```
info: DatabaseInitializer  Database reachable on attempt 1.
info: DatabaseInitializer  Applying 1 pending migration(s): InitialCreate
info: DatabaseInitializer  Migrations applied successfully.
info: DatabaseInitializer  Running seed data…
info: DatabaseInitializer  Seed complete.
info: Microsoft.Hosting.Lifetime  Now listening on: http://localhost:5000
info: IntegrationWorker  IntegrationWorker started.
```

---

## What Happens on First Startup

```
Program.cs
  │
  ├─ Build configuration
  │    appsettings.json → appsettings.{env}.json → env vars (prefix INTEGRATION_) → user-secrets
  │
  ├─ services.AddIntegrationMessaging(connectionString)
  │    registers DbContext, HttpClients, MemoryCache, CircuitBreaker,
  │    ResiliencePipelineFactory, JwtTokenProvider, MessageProcessor,
  │    MessageDispatcher, EndpointResolver, DeadLetterService,
  │    IntegrationWorker, health checks
  │
  └─ DatabaseInitializer.InitializeAsync()
       │
       ├─ WaitForDatabaseAsync()   retries up to 10×, 2 s apart
       ├─ MigrateAsync()           applies pending EF migrations (idempotent)
       └─ SeedAsync() → SeedData   inserts 5 systems + 8 endpoints (idempotent)

After InitializeAsync:
  └─ IntegrationWorker.ExecuteAsync()
       polls IntegrationMessageQueue every PollIntervalSeconds
```

---

## Tables Created (5)

| Table | Purpose |
|---|---|
| `IntegrationSystem` | System registry — 5 rows seeded |
| `IntegrationEndpoint` | Endpoint map per system × message type — 8 rows seeded |
| `IntegrationMessageQueue` | Pending outbound messages |
| `IntegrationMessage` | Full send/skip/fail history (audit) |
| `IntegrationDeadLetter` | Messages that exhausted all retries |

---

## Seeded Data

### IntegrationSystem (5 rows)

| Code | System | Auth | HTTP Client timeout |
|---|---|---|---|
| `WASTE_SOAP` | Port Waste Management | SOAP BasicAuth | 60 s |
| `BDZ_SITE_SCHEDULE` | BDZ Site Schedule | REST JWT | 60 s |
| `PORT_A` | Port Authority A — Ship Arrival | REST JWT | 30 s |
| `SHIPSAN` | SHIPSAN Maritime Sanitation | REST JWT | 15 s |
| `PCS` | Port Community System | REST mTLS | 30 s |

### IntegrationEndpoint (8 rows)

| System | MessageTypeName | Method | Path |
|---|---|---|---|
| `WASTE_SOAP` | WasteNotification | POST | `WasteService.svc` |
| `WASTE_SOAP` | WasteRequest | POST | `WasteService.svc` |
| `BDZ_SITE_SCHEDULE` | BdzSiteSchedule | POST | `schedules/import` |
| `PORT_A` | SSNNotification | POST | `arrivals/{EntityId}/ssn` |
| `PORT_A` | SSNNotification | DELETE | `arrivals/{EntityId}/ssn` |
| `SHIPSAN` | SHIPSANNotification | POST | `notifications` |
| `PCS` | PCSNotification | PUT | `declarations/{EntityId}` |

---

## Setting Credentials After First Run

Seed data inserts systems with `PasswordEncrypted = null`.  
Use `IPasswordEncryptor` to encrypt the real password and update the row:

```csharp
// In a migration, admin endpoint, or one-time setup script:
var encrypted = encryptor.Encrypt("real-plain-text-password");

await db.IntegrationSystems
    .Where(s => s.IntegrationSystemCode == "PORT_A")
    .ExecuteUpdateAsync(s => s
        .SetProperty(x => x.PasswordEncrypted, encrypted)
        .SetProperty(x => x.UserName, "porta_svc_real"));
```

Or directly in SQL (encrypt the value first via the running service):

```sql
UPDATE IntegrationSystem
SET PasswordEncrypted = '<base64-encrypted-value>',
    UserName          = 'porta_svc_real'
WHERE IntegrationSystemCode = 'PORT_A';
```

The `PCS` system uses mTLS — no username/password row needed; configure the client certificate in `HttpClientHandler`.

---

## Docker Compose

A full local stack with no manual SQL Server setup.

```yaml
# docker-compose.yml
services:
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      SA_PASSWORD: "YourStrong@Passw0rd"
      ACCEPT_EULA: "Y"
    ports:
      - "1433:1433"
    healthcheck:
      test: ["CMD", "/opt/mssql-tools/bin/sqlcmd",
             "-S", "localhost", "-U", "sa", "-P", "YourStrong@Passw0rd",
             "-Q", "SELECT 1"]
      interval: 10s
      retries: 10
      start_period: 30s

  integration:
    build: .
    depends_on:
      db:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      INTEGRATION_ConnectionStrings__IntegrationMessaging: >
        Server=db,1433;Database=IntegrationMessagingDb;
        User Id=sa;Password=YourStrong@Passw0rd;
        TrustServerCertificate=True;MultipleActiveResultSets=True;
      INTEGRATION_Encryption__Key: "your-32-char-minimum-secret-key!!"
    ports:
      - "5000:8080"
```

```bash
docker compose up --build
```

`DatabaseInitializer.WaitForDatabaseAsync()` retries up to 10 times with 2-second delays — the host survives SQL Server's container start-lag even without `depends_on`.

---

## Verify the Application

```bash
# Status
curl http://localhost:5000/

# Health (should return Healthy for both integration-db and integration-queue)
curl http://localhost:5000/health
```

Expected health response:

```json
{
  "status": "Healthy",
  "results": {
    "integration-db":    { "status": "Healthy" },
    "integration-queue": { "status": "Healthy" }
  }
}
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Missing required connection string 'IntegrationMessaging'` | Connection string not set | Set `ConnectionStrings:IntegrationMessaging` in appsettings or `INTEGRATION_ConnectionStrings__IntegrationMessaging` env var |
| `Database not reachable after 10 attempts` | SQL Server down or wrong host/port | Verify the connection string; check SQL Server is running |
| Startup crash about `Encryption:Key` | Key not set or too short | Set `Encryption:Key` (min 32 chars) via user-secrets or env var |
| `No pending migrations` but tables missing | Migration was never created | Run `dotnet ef migrations add InitialCreate ...` |
| Worker starts but no messages are dispatched | System disabled in DB | `UPDATE IntegrationSystem SET IsEnabled = 1` |
| Circuit OPEN logged immediately | `CircuitFailureThreshold = 0` in DB | Seed default is 5; check the row was inserted |
| Token fetched on every message | `TokenSkewSeconds` ≥ `ExpiresIn` | Reduce `TokenSkewSeconds`; floor is 30 s |
| Dead-letter INSERT FK violation | Running pre-v2 schema | Apply `DropDeadLetterQueueFK` migration |
