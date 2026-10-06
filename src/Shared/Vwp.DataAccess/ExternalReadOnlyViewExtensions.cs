using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vwp.DataAccess;

/// <summary>
/// Configures keyed EF Core entities to query consumer-local views over source tables
/// in another database on the same SQL Server instance.
/// </summary>
public static class ExternalReadOnlyViewExtensions
{
    /// <summary>
    /// Maps an entity to a local read-only view and records the three-part source name
    /// for the save-changes guard.
    /// </summary>
    /// <typeparam name="TEntity">The keyed entity type exposed by the local view.</typeparam>
    /// <param name="builder">The EF Core entity builder.</param>
    /// <param name="viewName">The view name in the current database.</param>
    /// <param name="sourceDatabase">The source database on the same SQL Server instance.</param>
    /// <param name="sourceTable">The source table name.</param>
    /// <param name="sourceSchema">The source table schema.</param>
    /// <param name="viewSchema">The local view schema.</param>
    /// <returns>The same builder so additional key, property, and navigation configuration can continue.</returns>
    public static EntityTypeBuilder<TEntity> ToExternalReadOnlyView<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string viewName,
        string sourceDatabase,
        string sourceTable,
        string sourceSchema = "dbo",
        string viewSchema = "dbo")
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewSchema);

        builder.ToView(viewName, viewSchema);
        builder.HasAnnotation(
            ReadOnlyViewSaveChangesInterceptor.ReadOnlyViewAnnotation,
            $"{sourceDatabase}.{sourceSchema}.{sourceTable}");

        return builder;
    }
}
