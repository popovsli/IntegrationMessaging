# IntegrationMessaging — First-Run Guide

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 |
| SQL Server | 2019+ (or Azure SQL / LocalDB for dev) |
| dotnet-ef tool | latest (`dotnet tool install --global dotnet-ef`) |

---

## Quick Start (one command)

```bash
# Linux / macOS / WSL
bash scripts/init.sh

# Windows PowerShell
.\scripts\init.ps1
```

Both scripts perform all 5 steps below automatically.

---

## Step-by-Step (manual)

### 1 — Set secrets (never commit real credentials)

```bash
cd IntegrationMessaging.Host

# Encryption key (min 32 chars)
dotnet user-secrets set "Encryption:Key" "your-32-char-minimum-secret-key!!"

# SOAP credentials
dotnet user-secrets set "WasteSoap:Username" "svc_waste"
dotnet user-secrets set "WasteSoap:Password" "real-password"

# System passwords stored encrypted in DB are set via the EncryptionService at runtime.
# Seed data rows have PasswordEncrypted=null — populate via admin UI or SQL after first run.
```

### 2 — Verify connection string

Edit `appsettings.Development.json` → `ConnectionStrings:IntegrationMessaging`
or set environment variable:
```bash
export INTEGRATION_ConnectionStrings__IntegrationMessaging="Server=...;Database=IntegrationMessagingDb;..."
```

### 3 — Create the initial migration

```bash
dotnet ef migrations add InitialCreate \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host \
  --output-dir Migrations
```

### 4 — Add the FK-fix migration (required if upgrading)

```bash
dotnet ef migrations add DropDeadLetterQueueFK \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host
```

### 5 — Apply migrations + seed

Migrations are applied automatically on startup by `DatabaseInitializer`.
If you prefer CLI:

```bash
dotnet ef database update \
  --project IntegrationMessaging \
  --startup-project IntegrationMessaging.Host
```

### 6 — Run

```bash
dotnet run --project IntegrationMessaging.Host --environment Development
```

---

## What Happens on First Startup

```
Program.cs
  └─ DatabaseInitializer.InitializeAsync()
       ├─ WaitForDatabaseAsync()   — retries up to 10× (useful in Docker)
       ├─ MigrateAsync()           — applies all pending EF migrations
       └─ SeedAsync()              — idempotent INSERT of systems + endpoints
  └─ IntegrationWorker (BackgroundService)
       └─ polls IntegrationMessageQueue every PollIntervalSeconds
```

---

## Tables Created

| Table | Purpose |
|---|---|
| `IntegrationSystem` | System registry (5 rows seeded) |
| `IntegrationEndpoint` | Endpoint map per system × message type (8 rows seeded) |
| `IntegrationMessageQueue` | Pending outbound messages |
| `IntegrationMessage` | Full send history (audit) |
| `IntegrationDeadLetter` | Messages exhausted all retries |

---

## Seeded Systems

| Code | Name | Auth |
|---|---|---|
| `WASTE_SOAP` | Port Waste Management | SOAP BasicAuth (via WasteSoapOptions) |
| `BDZ_SITE_SCHEDULE` | BDZ Site Schedule | REST JWT |
| `PORT_A` | Port Authority A — Ship Arrival | REST JWT |
| `SHIPSAN` | SHIPSAN Maritime Sanitation | REST JWT |
| `PCS` | Port Community System | REST mTLS certificate |

---

## Setting Real Credentials After First Run

Credentials per system are stored **encrypted** in `IntegrationSystem.PasswordEncrypted`
using the `Encryption:Key` from appsettings.  
Use the `EncryptionService` to encrypt before inserting:

```csharp
// Example helper call (run from a migration or admin controller):
var encrypted = encryptor.Encrypt("real-plain-text-password");
// UPDATE IntegrationSystem SET PasswordEncrypted = @encrypted WHERE IntegrationSystemCode = 'PORT_A'
```

---

## Docker Compose (example)

```yaml
services:
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      SA_PASSWORD: "YourStrong@Passw0rd"
      ACCEPT_EULA: "Y"
    ports: ["1433:1433"]

  integration:
    build: .
    depends_on: [db]
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      INTEGRATION_ConnectionStrings__IntegrationMessaging: >
        Server=db,1433;Database=IntegrationMessagingDb;
        User Id=sa;Password=YourStrong@Passw0rd;
        TrustServerCertificate=True;
      INTEGRATION_Encryption__Key: "your-32-char-key-here!!!!!!!!!!!!"
      INTEGRATION_WasteSoap__Username: "svc_waste"
      INTEGRATION_WasteSoap__Password: "waste-password"
```

`DatabaseInitializer.WaitForDatabaseAsync()` retries 10× with 2s delay —
handles SQL Server container start-lag automatically.
