using Microsoft.EntityFrameworkCore;
using Vwp.DataAccess;
using Vwp.Notifications.Api.Domain;

namespace Vwp.Notifications.Api.Data;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : ReadOnlyViewsDbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ContactInformation> ContactInformation => Set<ContactInformation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationType> NotificationTypes => Set<NotificationType>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView(
                viewName: "Accounts",
                sourceDatabase: "Accounts",
                sourceTable: "Accounts");
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Status).HasMaxLength(40).IsRequired();
            entity.Property(value => value.CreatedAt).HasColumnType("datetimeoffset(7)");
            entity.HasOne(value => value.ContactInformation)
                .WithOne(value => value.Account)
                .HasForeignKey<ContactInformation>(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ContactInformation>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView(
                viewName: "ContactInformation",
                sourceDatabase: "Accounts",
                sourceTable: "ContactInformation");
            entity.HasIndex(value => value.AccountId).IsUnique();
            entity.Property(value => value.Email).HasMaxLength(320).IsRequired();
            entity.Property(value => value.Phone).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Message).HasMaxLength(1000).IsRequired();
            entity.HasOne(value => value.Account)
                .WithMany()
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(value => value.NotificationType)
                .WithMany(value => value.Notifications)
                .HasForeignKey(value => value.NotificationTypeId);
        });

        modelBuilder.Entity<NotificationType>(entity =>
        {
            entity.ToTable("NotificationTypes");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(100).IsRequired();
        });
    }
}
