// Data/SeedData.cs
// Idempotent seed: uses INSERT IF NOT EXISTS so it is safe to run on every
// startup.  Real credentials are NEVER stored here — placeholders only.
// Override per environment via appsettings.{Environment}.json or Key Vault.

using IntegrationMessaging.Entities;
using Microsoft.EntityFrameworkCore;

namespace IntegrationMessaging.Data;

public static class SeedData
{
    public static async Task SeedAsync(IntegrationDbContext db, CancellationToken ct = default)
    {
        await SeedSystemsAsync(db, ct);
        await SeedEndpointsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    // ── IntegrationSystem rows ────────────────────────────────────────────

    private static async Task SeedSystemsAsync(IntegrationDbContext db, CancellationToken ct)
    {
        var existing = await db.IntegrationSystems
            .Select(s => s.IntegrationSystemCode)
            .ToHashSetAsync(ct);

        var systems = new List<IntegrationSystem>
        {
            new()
            {
                IntegrationSystemCode          = "WASTE_SOAP",
                SystemName                     = "Port Waste Management (WCF/SOAP)",
                IsEnabled                      = true,
                BaseAddress                    = "https://waste.portauth.example.com",
                // Credentials come from WasteSoapOptions (appsettings / Key Vault)
                UserName                       = null,
                PasswordEncrypted              = null,
                AuthUrl                        = null,      // SOAP BasicAuth — no token endpoint
                TokenSkewSeconds               = 0,
                ClientTimeoutSeconds           = 60,
                ClientRetryCount               = 3,
                QueueMessageRetryDelaySeconds  = 120,
                QueueMessageRetryCount         = 5,
                CircuitFailureThreshold        = 3,
                CircuitBreakDurationSeconds    = 120,
                UpdatedUtc                     = DateTimeOffset.UtcNow
            },
            new()
            {
                IntegrationSystemCode          = "BDZ_SITE_SCHEDULE",
                SystemName                     = "BDZ Site Schedule (REST / file upload)",
                IsEnabled                      = true,
                BaseAddress                    = "https://api.bdz.bg",
                UserName                       = "bdz_api_user",
                PasswordEncrypted              = null,      // set via Key Vault
                AuthUrl                        = "https://api.bdz.bg/auth/token",
                TokenSkewSeconds               = 30,
                ClientTimeoutSeconds           = 60,
                ClientRetryCount               = 2,
                QueueMessageRetryDelaySeconds  = 300,
                QueueMessageRetryCount         = 3,
                CircuitFailureThreshold        = 5,
                CircuitBreakDurationSeconds    = 180,
                UpdatedUtc                     = DateTimeOffset.UtcNow
            },
            new()
            {
                IntegrationSystemCode          = "PORT_A",
                SystemName                     = "Port Authority A — Ship Arrival (REST / JWT)",
                IsEnabled                      = true,
                BaseAddress                    = "https://api.port-a.example.com",
                UserName                       = "porta_svc",
                PasswordEncrypted              = null,
                AuthUrl                        = "https://api.port-a.example.com/oauth/token",
                TokenSkewSeconds               = 60,
                ClientTimeoutSeconds           = 30,
                ClientRetryCount               = 3,
                QueueMessageRetryDelaySeconds  = 60,
                QueueMessageRetryCount         = 10,
                CircuitFailureThreshold        = 5,
                CircuitBreakDurationSeconds    = 60,
                UpdatedUtc                     = DateTimeOffset.UtcNow
            },
            new()
            {
                IntegrationSystemCode          = "SHIPSAN",
                SystemName                     = "SHIPSAN Maritime Sanitation (REST)",
                IsEnabled                      = true,
                BaseAddress                    = "https://shipsan.emsa.example.com/api/v1",
                UserName                       = "shipsan_svc",
                PasswordEncrypted              = null,
                AuthUrl                        = "https://shipsan.emsa.example.com/auth/token",
                TokenSkewSeconds               = 30,
                ClientTimeoutSeconds           = 15,
                ClientRetryCount               = 3,
                QueueMessageRetryDelaySeconds  = 90,
                QueueMessageRetryCount         = 5,
                CircuitFailureThreshold        = 5,
                CircuitBreakDurationSeconds    = 90,
                UpdatedUtc                     = DateTimeOffset.UtcNow
            },
            new()
            {
                IntegrationSystemCode          = "PCS",
                SystemName                     = "Port Community System (REST / mTLS)",
                IsEnabled                      = true,
                BaseAddress                    = "https://pcs.portauth.example.com/api",
                // mTLS client certificate — set in HttpClientHandler, not credentials here
                UserName                       = null,
                PasswordEncrypted              = null,
                AuthUrl                        = null,
                TokenSkewSeconds               = 0,
                ClientTimeoutSeconds           = 30,
                ClientRetryCount               = 3,
                QueueMessageRetryDelaySeconds  = 60,
                QueueMessageRetryCount         = 10,
                CircuitFailureThreshold        = 5,
                CircuitBreakDurationSeconds    = 60,
                UpdatedUtc                     = DateTimeOffset.UtcNow
            }
        };

        foreach (var system in systems.Where(s => !existing.Contains(s.IntegrationSystemCode)))
            db.IntegrationSystems.Add(system);
    }

    // ── IntegrationEndpoint rows ──────────────────────────────────────────

    private static async Task SeedEndpointsAsync(IntegrationDbContext db, CancellationToken ct)
    {
        // Load existing (systemCode + messageTypeName) combos to avoid duplicates
        var existing = await db.IntegrationEndpoints
            .Select(e => new { e.IntegrationSystemCode, e.MessageTypeName })
            .ToListAsync(ct);

        var existingSet = existing
            .Select(e => $"{e.IntegrationSystemCode}:{e.MessageTypeName}")
            .ToHashSet();

        var endpoints = new List<IntegrationEndpoint>
        {
            // ── WASTE_SOAP ─────────────────────────────────────────────────
            new()
            {
                IntegrationSystemCode = "WASTE_SOAP",
                MessageTypeName       = "WasteNotification",
                EndpointPath          = "WasteService.svc",
                HttpMethod            = "POST",
                SoapAction            =
                    "http://tempuri.org/IWasteService/SubmitWasteNotification",
                Description           = "Submit or update a vessel waste notification"
            },
            new()
            {
                IntegrationSystemCode = "WASTE_SOAP",
                MessageTypeName       = "WasteRequest",
                EndpointPath          = "WasteService.svc",
                HttpMethod            = "POST",
                SoapAction            =
                    "http://tempuri.org/IWasteService/SubmitWasteRequest",
                Description           = "Submit a waste collection request for a vessel"
            },

            // ── BDZ_SITE_SCHEDULE ──────────────────────────────────────────
            new()
            {
                IntegrationSystemCode = "BDZ_SITE_SCHEDULE",
                MessageTypeName       = "BdzSiteSchedule",
                EndpointPath          = "schedules/import",
                HttpMethod            = "POST",
                SoapAction            = null,
                Description           = "Upload a BDZ site schedule file (multipart/form-data)"
            },

            // ── PORT_A ─────────────────────────────────────────────────────
            new()
            {
                IntegrationSystemCode = "PORT_A",
                MessageTypeName       = "SSNNotification",
                EndpointPath          = "arrivals/{EntityId}/ssn",
                HttpMethod            = "POST",
                SoapAction            = null,
                Description           = "Submit an SSN (SafeSeaNet) ship arrival notification"
            },
            new()
            {
                IntegrationSystemCode = "PORT_A",
                MessageTypeName       = "SSNNotification",        // DELETE uses same type
                EndpointPath          = "arrivals/{EntityId}/ssn",
                HttpMethod            = "DELETE",
                SoapAction            = null,
                Description           = "Cancel an SSN ship arrival notification"
            },

            // ── SHIPSAN ────────────────────────────────────────────────────
            new()
            {
                IntegrationSystemCode = "SHIPSAN",
                MessageTypeName       = "SHIPSANNotification",
                EndpointPath          = "notifications",
                HttpMethod            = "POST",
                SoapAction            = null,
                Description           = "Submit a SHIPSAN maritime sanitation notification"
            },

            // ── PCS ────────────────────────────────────────────────────────
            new()
            {
                IntegrationSystemCode = "PCS",
                MessageTypeName       = "PCSNotification",
                EndpointPath          = "declarations/{EntityId}",
                HttpMethod            = "PUT",
                SoapAction            = null,
                Description           = "Create or update a port community declaration"
            }
        };

        foreach (var ep in endpoints
            .Where(e => !existingSet.Contains($"{e.IntegrationSystemCode}:{e.MessageTypeName}")))
            db.IntegrationEndpoints.Add(ep);
    }
}
