// Data/IntegrationDbContext.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace IntegrationMessaging.Data;

public class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options)
    : DbContext(options)
{
    public DbSet<IntegrationSystem> IntegrationSystems => Set<IntegrationSystem>();
    public DbSet<IntegrationEndpoint> IntegrationEndpoints => Set<IntegrationEndpoint>();
    public DbSet<IntegrationMessageQueue> IntegrationMessageQueue => Set<IntegrationMessageQueue>();
    public DbSet<IntegrationMessage> IntegrationMessages => Set<IntegrationMessage>();
    public DbSet<IntegrationDeadLetter> IntegrationDeadLetters => Set<IntegrationDeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── IntegrationSystem ─────────────────────────────────────────────
        modelBuilder.Entity<IntegrationSystem>(e =>
        {
            e.HasKey(x => x.IntegrationSystemCode);
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50);
            e.Property(x => x.SystemName).HasMaxLength(500).IsRequired();
            e.Property(x => x.BaseAddress).HasMaxLength(2000).IsRequired();

            // Nullable — SOAP/API-key systems have no auth URL
            e.Property(x => x.AuthUrl).HasMaxLength(2000).IsRequired(false);

            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.PasswordEncrypted).HasMaxLength(2000);

            // Defaults for value types EF won't infer from C#
            e.Property(x => x.IsEnabled).HasDefaultValue(true);
            e.Property(x => x.ClientTimeoutSeconds).HasDefaultValue(30);
            e.Property(x => x.ClientRetryCount).HasDefaultValue(3);
            e.Property(x => x.TokenSkewSeconds).HasDefaultValue(30);
            e.Property(x => x.QueueMessageRetryDelaySeconds).HasDefaultValue(60);
            e.Property(x => x.QueueMessageRetryCount).HasDefaultValue(10);
            e.Property(x => x.CircuitFailureThreshold).HasDefaultValue(5);
            e.Property(x => x.CircuitBreakDurationSeconds).HasDefaultValue(60);

            // Auto-stamp on insert; application updates on change
            e.Property(x => x.UpdatedUtc).HasDefaultValueSql("SYSDATETIMEOFFSET()");
        });

        // ── IntegrationEndpoint ───────────────────────────────────────────
        modelBuilder.Entity<IntegrationEndpoint>(e =>
        {
            e.ToTable("IntegrationEndpoint");
            e.HasKey(x => x.Id);
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.MessageTypeName).HasMaxLength(500).IsRequired();
            e.Property(x => x.EndpointPath).HasMaxLength(2000).IsRequired();
            e.Property(x => x.HttpMethod).HasMaxLength(10).HasDefaultValue("POST");
            e.Property(x => x.SoapAction).HasMaxLength(500);
            e.Property(x => x.Description).HasMaxLength(1000);

            // Non-unique: same MessageTypeName can have different endpoints
            // per operation (e.g. different SoapAction for Create vs Delete)
            e.HasIndex(x => new { x.IntegrationSystemCode, x.MessageTypeName });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.Endpoints)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── IntegrationMessageQueue ───────────────────────────────────────
        modelBuilder.Entity<IntegrationMessageQueue>(e =>
        {
            e.ToTable("IntegrationMessageQueue");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.MessageTypeName).HasMaxLength(500).IsRequired();

            e.Property(x => x.MessageOperation)
             .HasConversion<string>()
             .HasMaxLength(50)
             .HasDefaultValue(MessageOperation.Update);

            e.Property(x => x.Payload).HasColumnType("varchar(max)").IsRequired();

            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(50)
             .HasDefaultValue(QueueMessageStatus.Queued);

            e.Property(x => x.AttemptCount).HasDefaultValue(0);
            e.Property(x => x.CreationTime).HasDefaultValueSql("GETUTCDATE()");
            e.Property(x => x.LastError).HasColumnType("varchar(max)");

            // Requeue tracking
            e.Property(x => x.RequeuedFromMessageId);          // nullable int FK
            e.Property(x => x.RequeuedBy).HasMaxLength(256);
            e.Property(x => x.RequeuedAtUtc);

            // Polling index: pick next ready row efficiently
            e.HasIndex(x => new { x.Status, x.NextAttempt, x.LockedUntil });
            // Deduplication / ordering check
            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode, x.MessageOperation });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.QueueMessages)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── IntegrationMessage (history) ──────────────────────────────────
        modelBuilder.Entity<IntegrationMessage>(e =>
        {
            e.ToTable("IntegrationMessage");
            e.HasKey(x => x.IntegrationMessageId);
            e.Property(x => x.IntegrationMessageId).UseIdentityColumn();
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();

            e.Property(x => x.Operation)
             .HasConversion<string>()
             .HasMaxLength(50)
             .IsRequired();

            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.RequestPayload).HasColumnType("varchar(max)").IsRequired();

            // Nullable — typed SOAP void operations produce no response payload
            e.Property(x => x.ResponsePayload).HasColumnType("varchar(max)").IsRequired(false);

            e.Property(x => x.Error).HasColumnType("varchar(max)");
            e.Property(x => x.RetryCount).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            e.Property(x => x.UpdatedAt);
            e.Property(x => x.LastAttemptAtUtc);
            e.Property(x => x.HttpStatusCode);   // REST only, null for SOAP

            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode, x.Operation, x.Status });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.MessageHistory)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── IntegrationDeadLetter ─────────────────────────────────────────
        modelBuilder.Entity<IntegrationDeadLetter>(e =>
        {
            e.ToTable("IntegrationDeadLetter");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.MessageTypeName).HasMaxLength(500).IsRequired();
            e.Property(x => x.MessageOperation).HasMaxLength(50).IsRequired();
            e.Property(x => x.Payload).HasColumnType("varchar(max)").IsRequired();
            e.Property(x => x.LastError).HasColumnType("varchar(max)");
            e.Property(x => x.AttemptCount).HasDefaultValue(0);
            e.Property(x => x.DeadLetteredAtUtc).HasDefaultValueSql("GETUTCDATE()");
            e.Property(x => x.ResolvedByUser).HasMaxLength(256);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);

            e.Property(x => x.Resolution)
             .HasConversion<string>()
             .HasMaxLength(50);

            // FK to original queue row — for traceability
            e.Property(x => x.OriginalQueueId).IsRequired();
            e.HasOne<IntegrationMessageQueue>()
             .WithMany()
             .HasForeignKey(x => x.OriginalQueueId)
             .OnDelete(DeleteBehavior.Restrict);

            // FK to history record — nullable, only set when message was sent before dying
            e.Property(x => x.IntegrationMessageId);
            e.HasOne<IntegrationMessage>()
             .WithMany()
             .HasForeignKey(x => x.IntegrationMessageId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.IntegrationSystemCode, x.ResolvedAtUtc });
            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany()
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}