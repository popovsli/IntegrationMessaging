# IntegrationMessaging

A .NET 10 background-worker library that dispatches outbound integration messages to multiple external systems over REST/JWT and SOAP/WCF transports.  
Messages are persisted in SQL Server via Entity Framework Core, processed in batches by a hosted worker, and protected by per-system circuit breakers, Polly retry pipelines, and dead-letter handling.

---

## Solution Structure

```
IntegrationMessaging.sln
│
├── IntegrationMessaging/                  ← class library (the reusable core)
│   ├── Configuration/
│   │   └── IntegrationMessagingOptions.cs   worker tuning (batch, poll, lock, …)
│   ├── Data/
│   │   ├── DatabaseInitializer.cs           wait → migrate → seed on startup
│   │   ├── IntegrationDbContext.cs          EF Core context + fluent config
│   │   └── SeedData.cs                      idempotent system + endpoint seed
│   ├── Entities/
│   │   ├── Enums/                           DeadLetterResolution, MessageOperation,
│   │   │                                    QueueMessageStatus
│   │   ├── IntegrationDeadLetter.cs
│   │   ├── IntegrationEndpoint.cs
│   │   ├── IntegrationMessage.cs            send-history / audit
│   │   ├── IntegrationMessageQueue.cs       pending outbound work
│   │   └── IntegrationSystem.cs            system registry + per-system config
│   ├── Exceptions/
│   │   └── IntegrationMessagingException.cs
│   ├── Extensions/
│   │   └── ServiceCollectionExtensions.cs  AddIntegrationMessaging(connStr)
│   ├── HealthChecks/
│   │   └── IntegrationHealthCheck.cs        integration-queue health probe
│   ├── Models/
│   │   ├── EndpointResolution.cs
│   │   ├── IntegrationRequest.cs
│   │   ├── IntegrationResponse.cs
│   │   └── SendContext.cs
│   ├── Services/
│   │   ├── CircuitBreaker/                  in-memory, per-system, half-open probe
│   │   ├── Clients/
│   │   │   ├── Base/                        RestIntegrationClientBase,
│   │   │   │                                SoapIntegrationClientBase,
│   │   │   │                                TypedSoapClientBase
│   │   │   ├── Soap/                        SoapChannelFactoryManager (cached),
│   │   │   │                                SoapFaultParser, SoapChannelHelper
│   │   │   ├── IIntegrationClient.cs
│   │   │   └── UrlBuilder.cs
│   │   ├── DeadLetterService.cs
│   │   ├── EndpointResolver.cs              DB lookup + MemoryCache
│   │   ├── Handlers/
│   │   │   ├── HandlerKeys.cs
│   │   │   └── IIntegrationMessageHandler.cs
│   │   ├── IntegrationClients/              concrete clients + handlers
│   │   │   ├── BdzSiteScheduleClient.cs
│   │   │   ├── PortAuthorityAClient.cs
│   │   │   ├── WasteSoapClient.cs
│   │   │   ├── Contracts/IWasteService.cs
│   │   │   ├── DTOs/WasteNotificationDto.cs
│   │   │   └── Handlers/
│   │   │       ├── BdzSiteScheduleHandler.cs
│   │   │       ├── WasteRequestHandler.cs
│   │   │       └── WasteSoapNotificationHandler.cs
│   │   ├── MessageDispatcher.cs
│   │   ├── MessageProcessor.cs             core batch/send/retry/dead-letter logic
│   │   ├── Resilience/                     Polly pipelines, fingerprint-cached
│   │   ├── Security/                       AES-256 password encryptor
│   │   └── TokenProviders/                 JWT token cache (30 s floor)
│   └── Workers/
│       └── IntegrationWorker.cs            BackgroundService (poll loop)
│
├── IntegrationMessaging.Host/              ← runnable host (ASP.NET Core)
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── appsettings.Production.json
│
├── scripts/
│   ├── init.sh                             one-command first-run (Linux/macOS/WSL)
│   └── init.ps1                            one-command first-run (Windows PowerShell)
│
└── sql/
    └── seed.sql                            manual SQL seed (alternative to auto-seed)
```

---

## Architecture

```
                    ┌─────────────────────────────────────────────────┐
                    │                 IntegrationWorker                │
                    │  BackgroundService — polls every PollInterval    │
                    └──────────────────────┬──────────────────────────┘
                                           │ IMessageProcessor
                    ┌──────────────────────▼──────────────────────────┐
                    │              MessageProcessor                    │
                    │                                                  │
                    │  1. Requeue stale Processing rows (crash guard)  │
                    │  2. Claim batch (WorkerStamp + LockedUntil)      │
                    │  3. For each message:                            │
                    │     a. Skip if system disabled or circuit OPEN   │
                    │     b. CollapseRedundantUpdates (dedup)          │
                    │     c. EnsureCreateWasSentAsync (prereq guard)   │
                    │     d. SendMessageAsync                          │
                    │     e. Record success / failure / dead-letter    │
                    └──────┬──────────────────────────────────────────┘
                           │ IMessageDispatcher
          ┌────────────────▼─────────────────────────────────┐
          │                 MessageDispatcher                 │
          │   Resolves IIntegrationMessageHandler (keyed)     │
          │   → shapes payload                                │
          │   Resolves IIntegrationClient (keyed by system)   │
          │   → sends message                                 │
          └────────┬──────────────────────────────────────────┘
                   │
       ┌───────────┼──────────────┐
       ▼           ▼              ▼
 WasteSoapClient  BdzSiteSched  PortAuthorityAClient …
 (SOAP/WCF)       uleClient     (REST/JWT)
       │           │              │
  SoapChannel    HttpClient    HttpClient
  Factory +      (60 s)        (30 s +
  BasicAuth                    StandardResil.)
       │                          │
  IWasteService             JWT Token
  (WCF proxy)               (ITokenProvider)
```

### Message Lifecycle

```
INSERT → Queued
            │
     Worker claims batch
            │
         Processing ──── circuit OPEN or system disabled ──▶ Queued (released)
            │
     Send attempt
            │
      ┌─────┴──────────┐
   Success           Failure
      │                 │
    Sent             attempts < retryCount?
   (queue row                │
    deleted,          Yes ── Queued (NextAttempt set)
    history row        No ── Dead-lettered
    inserted)
            │
     CollapseRedundantUpdates:
       superseded Update → Skipped
```

---

## Integrated Systems

| Code | System | Transport | Auth |
|---|---|---|---|
| `WASTE_SOAP` | Port Waste Management | SOAP/WCF | BasicAuth |
| `BDZ_SITE_SCHEDULE` | BDZ Site Schedule | REST (`multipart/form-data`) | JWT |
| `PORT_A` | Port Authority A — Ship Arrival | REST | JWT |
| `SHIPSAN` | SHIPSAN Maritime Sanitation | REST | JWT |
| `PCS` | Port Community System | REST | mTLS certificate |

### Seeded Endpoints (8 rows)

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

## Database Schema

Five tables, all owned by the `IntegrationMessaging` library:

| Table | PK | Key relationships |
|---|---|---|
| `IntegrationSystem` | `IntegrationSystemCode` (varchar 50) | Parent of the four below |
| `IntegrationEndpoint` | `Id` (identity) | FK → `IntegrationSystem` (Restrict) |
| `IntegrationMessageQueue` | `Id` (identity) | FK → `IntegrationSystem` (Restrict) |
| `IntegrationMessage` | `IntegrationMessageId` (identity) | FK → `IntegrationSystem` (Restrict) |
| `IntegrationDeadLetter` | `Id` (identity) | FK → `IntegrationSystem` (Restrict); `OriginalQueueId` = audit int (index only, **no FK** — queue row is deleted before DL insert) |

---

## Configuration

### `IntegrationMessaging` section (appsettings)

| Key | Type | Default | Dev override | Range |
|---|---|---|---|---|
| `BatchSize` | int | 50 | 10 | 1–1000 |
| `PollIntervalSeconds` | int | 5 | 3 | 1–3600 |
| `LockDurationMinutes` | int | 5 | 2 | 1–60 |
| `EndpointCacheMinutes` | int | 5 | 1 | 1–60 |
| `StaleRequeueLimit` | int | 100 | 20 | 1–10000 |

`StaleRequeueLimit` caps the number of stale `Processing` rows that can be requeued per tick — prevents a single unbounded `UPDATE` after a mass crash.

### `Encryption` section

| Key | Notes |
|---|---|
| `Encryption:Key` | **Required.** AES-256 encryption key. Minimum 32 characters. Never commit to source control. |

### `IntegrationSystem` DB columns (per-system runtime config)

| Column | Default | Description |
|---|---|---|
| `IsEnabled` | `true` | Master on/off switch for the system |
| `ClientTimeoutSeconds` | 30 | Per-attempt HTTP/SOAP timeout |
| `ClientRetryCount` | 3 | Polly retry attempts |
| `TokenSkewSeconds` | 30 | Shaved from token TTL before caching (floor 30 s) |
| `QueueMessageRetryDelaySeconds` | 60 | Seconds between delivery retries |
| `QueueMessageRetryCount` | 10 | Max delivery attempts before dead-lettering |
| `CircuitFailureThreshold` | 5 | Consecutive failures before circuit opens |
| `CircuitBreakDurationSeconds` | 60 | How long the circuit stays open |

### Named HTTP Clients

| Name | Timeout | Resilience |
|---|---|---|
| `BDZ_SITE_SCHEDULE` | 60 s | none (large file uploads) |
| `PORT_A` | 30 s | `AddStandardResilienceHandler` |
| `PCS` | 30 s | `AddStandardResilienceHandler` |
| `SHIPSAN` | 15 s | `AddStandardResilienceHandler` |
| `IntegrationMessaging.Auth` | default | `AddStandardResilienceHandler` (2 retries, 500 ms) |

---

## Resilience Design

### Polly Pipeline (per system, fingerprint-cached)

```
outer timeout (ClientTimeoutSeconds)
  └─ inner retry  (ClientRetryCount, exponential back-off 2 s base, jitter)
       REST: handles HttpRequestException + HTTP 5xx
       SOAP: handles CommunicationException + TimeoutException
```

Pipelines are cached in a `ConcurrentDictionary` keyed by `(SystemCode, SHA-256(ClientRetryCount|ClientTimeoutSeconds))`.  
If either column changes in the DB, the next call automatically rebuilds the pipeline.

### Circuit Breaker (per system, IMemoryCache)

```
Closed ──[CircuitFailureThreshold failures]──▶ Open
Open   ──[CircuitBreakDurationSeconds elapsed]──▶ Half-Open (probe)
Half-Open ──[probe succeeds]──▶ Closed
Half-Open ──[probe fails]──▶ Open (break duration resets)
```

All state mutations are `lock`-guarded.  
Manual reset: `ICircuitBreakerService.Reset(systemCode)`.

### JWT Token Caching

- `ITokenProvider.GetTokenAsync` POST to `AuthUrl` with `{username, password}` JSON.
- Cached with TTL = `ExpiresIn − TokenSkewSeconds`, floored at **30 seconds**.
- Auth HTTP client uses `AddStandardResilienceHandler` (2 retries) to handle transient token-endpoint failures.

---

## Security

| What | How |
|---|---|
| System passwords | AES-256-GCM via `AesPasswordEncryptor`, stored as `PasswordEncrypted` in `IntegrationSystem` |
| Encryption key | From `Encryption:Key` in configuration — must be ≥ 32 characters |
| SOAP credentials | Username from `IntegrationSystem.UserName`; password decrypted at call time |
| mTLS (PCS) | Client certificate configured in `HttpClientHandler` — no username/password |
| Secrets management | Use `dotnet user-secrets` for dev; environment variables or Azure Key Vault for production |

---

## HTTP Endpoints

The host exposes three HTTP endpoints:

| Route | Description |
|---|---|
| `GET /` | Status JSON: `name`, `environment`, `status`, `utc` |
| `GET /health` | Aggregated health: `integration-db` + `integration-queue` |
| `GET /openapi/...` | OpenAPI spec — **Development only** |

---

## Adding a New Message Type

1. Add a constant to `HandlerKeys`:
   ```csharp
   public const string MyNewType = "MyNewType";
   ```
2. Create a class implementing `IIntegrationMessageHandler`.
3. Register it in `ServiceCollectionExtensions` (or after `AddIntegrationMessaging`):
   ```csharp
   services.AddKeyedScoped<IIntegrationMessageHandler, MyNewTypeHandler>(HandlerKeys.MyNewType);
   ```
4. Insert a row into `IntegrationEndpoint` for each target system.

## Adding a New Integration System

1. Insert a row into `IntegrationSystem` with the required fields.
2. Register the named `HttpClient` in `ServiceCollectionExtensions`:
   ```csharp
   services.AddHttpClient("MY_SYSTEM", c => c.Timeout = TimeSpan.FromSeconds(30))
           .AddStandardResilienceHandler();
   ```
3. Implement `IIntegrationClient` (or extend `RestIntegrationClientBase` / `SoapIntegrationClientBase`).
4. Register the client:
   ```csharp
   services.AddKeyedScoped<IIntegrationClient, MySystemClient>("MY_SYSTEM");
   ```
5. Insert rows into `IntegrationEndpoint` for all message types the system supports.
6. Set `PasswordEncrypted` via `IPasswordEncryptor.Encrypt(plainText)` if the system uses JWT auth.

---

## Key Behavioural Rules

- **Update/Delete prerequisite** — an Update or Delete cannot be dispatched unless a successful Create exists in `IntegrationMessage` for the same `EntityId` + `IntegrationSystemCode`. If only a pending Create is queued, it is sent automatically first.
- **Redundant-update collapse** — when multiple Queued/Processing Update messages exist for the same entity, only the most recent is sent; older ones are marked `Skipped`.
- **Distributed lock safety** — `LockedUntil` + `WorkerStamp` prevent double-processing in scaled-out deployments.
- **Stale-lock recovery** — Processing rows whose `LockedUntil` has passed are requeued at the start of every tick, capped at `StaleRequeueLimit`.
- **Dead-letter insert order** — the DL row is `SaveChanges`-committed **before** the queue row is deleted, preventing the FK to `IntegrationSystem` from being violated.
