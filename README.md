# IntegrationMessaging

A .NET 10 library for processing outbound integration messages via MSSQL +
Entity Framework Core, with support for REST/JWT and SOAP/WCF transports.

## Architecture

```
IntegrationMessageQueue (DB)
        │
        ▼
  MessageProcessor
        │
        ├── EnsureCreateWasSentAsync  ← prerequisite guard
        │
        ▼
  MessageDispatcher
        ├── IIntegrationMessageHandler  (keyed by MessageTypeName)  → shapes payload
        └── IIntegrationClient          (keyed by ClientType)        → sends message
```

## Quick Start

```bash
# 1. Apply migrations
dotnet ef migrations add InitialCreate --project IntegrationMessaging --startup-project IntegrationMessaging.Host
dotnet ef database update             --project IntegrationMessaging --startup-project IntegrationMessaging.Host

# 2. Seed data
sqlcmd -S . -d IntegrationDb -i sql/seed.sql

# 3. Run
dotnet run --project IntegrationMessaging.Host
```

## Adding a New Message Type

1. Add a constant to `HandlerKeys`
2. Create a class implementing `IIntegrationMessageHandler`
3. Register in `ServiceCollectionExtensions`:
   ```csharp
   services.AddKeyedScoped<IIntegrationMessageHandler, MyHandler>(HandlerKeys.MyType);
   ```
4. Insert a row into `IntegrationEndpoint` for each system that receives this type

## Adding a New Transport

1. Add a constant to `ClientKeys`
2. Create a class implementing `IIntegrationClient`
3. Register:
   ```csharp
   services.AddKeyedScoped<IIntegrationClient, MyClient>(ClientKeys.MyTransport);
   ```
4. Set `IntegrationSystem.ClientType = "MY_TRANSPORT"` for target systems

## Key Rules

- **Update/Delete prerequisite**: An Update or Delete message cannot be sent
  unless a successful Create exists in `IntegrationMessage` for the same
  `EntityId` + `IntegrationSystemCode`. If a pending Create exists in the queue,
  it is sent automatically first.
- **Distributed locking**: `LockedUntil` prevents double-processing in
  scaled-out deployments.
- **Circuit breaker**: Per-system, in-memory. Opens after N consecutive failures,
  recovers after `CircuitBreakDurationSeconds`.
- **Retry scheduling**: Failed messages are rescheduled via `NextAttempt`
  using `QueueMessageRetryDelaySeconds` from `IntegrationSystem`.
