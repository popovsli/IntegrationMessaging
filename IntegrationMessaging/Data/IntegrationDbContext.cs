using IntegrationMessaging.Entities;
using IntegrationMessaging.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace IntegrationMessaging.Data;

public class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options)
    : DbContext(options)
{
    public DbSet<IntegrationSystem> IntegrationSystem => Set<IntegrationSystem>();
    public DbSet<IntegrationEndpoint> IntegrationEndpoint => Set<IntegrationEndpoint>();
    public DbSet<IntegrationMessageQueue> IntegrationMessageQueue => Set<IntegrationMessageQueue>();
    public DbSet<IntegrationMessage> IntegrationMessage => Set<IntegrationMessage>();
    public DbSet<IntegrationDeadLetter> IntegrationDeadLetter => Set<IntegrationDeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationSystem>(e =>
        {
            e.HasKey(x => x.IntegrationSystemCode);
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.PasswordSecret).HasMaxLength(2000);
            e.Property(x => x.SystemName).HasMaxLength(500).IsRequired();
            e.Property(x => x.BaseAddress).HasMaxLength(2000).IsRequired();
            e.Property(x => x.EndpointPath).HasMaxLength(2000).IsRequired();
            e.Property(x => x.AuthEndpointPath).HasMaxLength(2000).IsRequired();
            e.Property(x => x.ClientType).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContractType).HasMaxLength(100).IsRequired();
            e.Property(x => x.FormatPreference).HasMaxLength(10).HasDefaultValue("JSON");
            e.Property(x => x.HeadersJson).HasColumnType("varchar(max)").HasDefaultValue("{}");
        });

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

            e.HasIndex(x => new { x.IntegrationSystemCode, x.MessageTypeName }).IsUnique();

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.Endpoints)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntegrationMessageQueue>(e =>
        {
            e.ToTable("IntegrationMessageQueue");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.MessageOperation)
             .HasConversion<string>().HasMaxLength(50)
             .HasDefaultValue(MessageOperation.Update);
            e.Property(x => x.Payload).HasColumnType("varchar(max)").IsRequired();
            e.Property(x => x.Status)
             .HasConversion<string>().HasMaxLength(50)
             .HasDefaultValue(QueueMessageStatus.Queued);
            e.Property(x => x.LastError).HasColumnType("varchar(max)");
            e.Property(x => x.MessageTypeName).HasMaxLength(500).IsRequired();

            e.HasIndex(x => new { x.Status, x.NextAttempt, x.LockedUntil });
            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode, x.MessageOperation });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.QueueMessages)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntegrationMessage>(e =>
        {
            e.ToTable("IntegrationMessage");
            e.HasKey(x => x.IntegrationMessageId);
            e.Property(x => x.IntegrationMessageId).UseIdentityColumn();
            e.Property(x => x.IntegrationSystemCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(50).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.RequestPayload).HasColumnType("varchar(max)").IsRequired();
            e.Property(x => x.ResponsePayload).HasColumnType("varchar(max)").IsRequired();
            e.Property(x => x.Error).HasColumnType("varchar(max)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode, x.Operation, x.Status });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany(s => s.MessageHistory)
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // In IntegrationDbContext.OnModelCreating:
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
            e.Property(x => x.ResolvedByUser).HasMaxLength(256);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);
            e.Property(x => x.Resolution).HasConversion<string>().HasMaxLength(50);

            // Find all unresolved dead letters for a system quickly
            e.HasIndex(x => new { x.IntegrationSystemCode, x.ResolvedAtUtc });
            e.HasIndex(x => new { x.EntityId, x.IntegrationSystemCode });

            e.HasOne(x => x.IntegrationSystem)
             .WithMany()
             .HasForeignKey(x => x.IntegrationSystemCode)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
