using Microsoft.EntityFrameworkCore;

namespace Vwp.DataAccess;

/// <summary>
/// Validates external read-only view mappings while allowing writes to consumer-owned tables.
/// </summary>
public abstract class ReadOnlyViewsDbContext : DbContext
{
    /// <summary>
    /// Initializes a consumer context with untracked queries by default.
    /// </summary>
    /// <param name="options">Context configuration.</param>
    protected ReadOnlyViewsDbContext(DbContextOptions options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new ReadOnlyViewConvention());
    }
}
