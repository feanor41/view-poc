using Microsoft.EntityFrameworkCore;
using Vwp.Cases.Api.Domain;
using Vwp.DataAccess;

namespace Vwp.Cases.Api.Data;

public sealed class CasesDbContext(DbContextOptions<CasesDbContext> options)
    : ReadOnlyViewsDbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<ContactInformation> ContactInformation => Set<ContactInformation>();
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<CaseType> CaseTypes => Set<CaseType>();
    public DbSet<CaseGroup> CaseGroup => Set<CaseGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView("Accounts");
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Status).HasMaxLength(40).IsRequired();
            entity.Property(value => value.CreatedAt).HasColumnType("datetimeoffset(7)");
            entity.HasMany(value => value.Assets)
                .WithOne(value => value.Account)
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(value => value.ContactInformation)
                .WithOne(value => value.Account)
                .HasForeignKey<ContactInformation>(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Asset>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView("Assets");
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Kind).HasMaxLength(80).IsRequired();
            entity.Property(value => value.Value).HasPrecision(18, 2);
        });

        modelBuilder.Entity<ContactInformation>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView("ContactInformation");
            entity.HasIndex(value => value.AccountId).IsUnique();
            entity.Property(value => value.Email).HasMaxLength(320).IsRequired();
            entity.Property(value => value.Phone).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<Case>(entity =>
        {
            entity.ToTable("Cases");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Title).HasMaxLength(200).IsRequired();
            entity.HasOne(value => value.Account)
                .WithMany()
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(value => value.Asset)
                .WithMany()
                .HasForeignKey(value => value.AssetId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(value => value.CaseType)
                .WithMany(value => value.Cases)
                .HasForeignKey(value => value.CaseTypeId);
            entity.HasOne(value => value.CaseGroup)
                .WithMany(value => value.Cases)
                .HasForeignKey(value => value.CaseGroupId);
        });

        modelBuilder.Entity<CaseType>(entity =>
        {
            entity.ToTable("CaseTypes");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<CaseGroup>(entity =>
        {
            entity.ToTable("CaseGroup");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(100).IsRequired();
        });
    }
}
