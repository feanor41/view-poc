using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vwp.DataAccess;

/// <summary>
/// Configures keyed EF Core entities to query consumer-local views over source tables
/// in another database on the same SQL Server instance.
/// </summary>
public static class ExternalReadOnlyViewExtensions
{
    internal const string ReadOnlyViewAnnotation = "Vwp:ReadOnlyView";

    /// <summary>
    /// Maps a keyed entity exclusively to a local view and marks it for model validation.
    /// Native EF Core view-only mapping rejects persistence; SQL permissions protect raw SQL.
    /// </summary>
    /// <remarks>
    /// Use with <see cref="ReadOnlyViewsDbContext"/> to reject conflicting writable mappings.
    /// The view's source database and table belong in its SQL definition, not in this mapping.
    /// </remarks>
    /// <typeparam name="TEntity">The keyed entity type exposed by the local view.</typeparam>
    /// <param name="builder">The EF Core entity builder.</param>
    /// <param name="viewName">The view name in the current database.</param>
    /// <param name="viewSchema">The local view schema.</param>
    /// <returns>The same builder so additional key, property, and navigation configuration can continue.</returns>
    public static EntityTypeBuilder<TEntity> ToExternalReadOnlyView<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string viewName,
        string viewSchema = "dbo")
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewSchema);

        builder.ToView(viewName, viewSchema);
        builder.HasAnnotation(ReadOnlyViewAnnotation, true);

        return builder;
    }
}
