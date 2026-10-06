using Microsoft.EntityFrameworkCore;
using Vwp.Accounts.Api.Domain;

namespace Vwp.Accounts.Api.Data;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AccountHistory> AccountHistory => Set<AccountHistory>();
    public DbSet<ContactInformation> ContactInformation => Set<ContactInformation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("Accounts");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Status).HasMaxLength(40).IsRequired();
            entity.Property(value => value.CreatedAt).HasColumnType("datetimeoffset(7)");
            entity.HasMany(value => value.Assets)
                .WithOne(value => value.Account)
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(value => value.History)
                .WithOne(value => value.Account)
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(value => value.ContactInformation)
                .WithOne(value => value.Account)
                .HasForeignKey<ContactInformation>(value => value.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Asset>(entity =>
        {
            entity.ToTable("Assets");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Kind).HasMaxLength(80).IsRequired();
            entity.Property(value => value.Value).HasPrecision(18, 2);
        });

        modelBuilder.Entity<AccountHistory>(entity =>
        {
            entity.ToTable("AccountHistory");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Description).HasMaxLength(1000).IsRequired();
            entity.Property(value => value.OccurredAt).HasColumnType("datetimeoffset(7)");
        });

        modelBuilder.Entity<ContactInformation>(entity =>
        {
            entity.ToTable("ContactInformation");
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.AccountId).IsUnique();
            entity.Property(value => value.Email).HasMaxLength(320).IsRequired();
            entity.Property(value => value.Phone).HasMaxLength(50).IsRequired();
        });
    }
}
