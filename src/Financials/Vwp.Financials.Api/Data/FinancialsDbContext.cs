using Microsoft.EntityFrameworkCore;
using Vwp.DataAccess;
using Vwp.Financials.Api.Domain;

namespace Vwp.Financials.Api.Data;

public sealed class FinancialsDbContext(DbContextOptions<FinancialsDbContext> options)
    : ReadOnlyViewsDbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<PaymentMean> PaymentMeans => Set<PaymentMean>();
    public DbSet<CollectionOrder> CollectionOrders => Set<CollectionOrder>();

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
        });

        modelBuilder.Entity<Asset>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.ToExternalReadOnlyView("Assets");
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.Kind).HasMaxLength(80).IsRequired();
            entity.Property(value => value.Value).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.ToTable("Operations");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Description).HasMaxLength(500).IsRequired();
            entity.Property(value => value.Amount).HasPrecision(18, 2);
            entity.HasOne(value => value.Account)
                .WithMany()
                .HasForeignKey(value => value.AccountId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasMany(value => value.Transactions)
                .WithOne(value => value.Operation)
                .HasForeignKey(value => value.OperationId);
            entity.HasMany(value => value.CollectionOrders)
                .WithOne(value => value.Operation)
                .HasForeignKey(value => value.OperationId);
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("Transactions");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Amount).HasPrecision(18, 2);
            entity.Property(value => value.PaymentMeanId).HasColumnName("PaymentMeansId");
            entity.HasOne(value => value.PaymentMean)
                .WithMany(value => value.Transactions)
                .HasForeignKey(value => value.PaymentMeanId);
        });

        modelBuilder.Entity<PaymentMean>(entity =>
        {
            entity.ToTable("PaymentMeans");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<CollectionOrder>(entity =>
        {
            entity.ToTable("CollectionOrders");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.DueAt).HasColumnType("datetimeoffset(7)");
        });
    }
}
