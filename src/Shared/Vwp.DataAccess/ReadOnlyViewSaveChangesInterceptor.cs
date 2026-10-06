using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Vwp.DataAccess;

/// <summary>
/// Identifies an attempted change to an entity mapped by
/// <see cref="ExternalReadOnlyViewExtensions.ToExternalReadOnlyView{TEntity}"/>.
/// </summary>
public sealed class ReadOnlyViewWriteAttemptException : InvalidOperationException
{
    /// <summary>
    /// Creates an exception for an attempted write to a view-backed entity.
    /// </summary>
    /// <param name="entityName">The entity type whose state was changed.</param>
    /// <param name="sourceName">The source table recorded by the mapping extension.</param>
    /// <param name="state">The attempted EF Core state.</param>
    public ReadOnlyViewWriteAttemptException(string entityName, string sourceName, EntityState state)
        : base($"Cannot persist {state} changes for view-backed entity '{entityName}' (source '{sourceName}').")
    {
    }
}

/// <summary>
/// Rejects Added, Modified, or Deleted state for entities mapped to external views.
/// Database permissions remain necessary to guard bulk SQL and raw SQL paths.
/// </summary>
public sealed class ReadOnlyViewSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// EF model annotation used by <see cref="ExternalReadOnlyViewExtensions"/>.
    /// </summary>
    public const string ReadOnlyViewAnnotation = "Vwp:ExternalReadOnlyViewSource";

    /// <summary>
    /// Reusable stateless interceptor instance for consumer contexts.
    /// </summary>
    public static ReadOnlyViewSaveChangesInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        RejectReadOnlyViewChanges(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RejectReadOnlyViewChanges(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void RejectReadOnlyViewChanges(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var sourceName = entry.Metadata.FindAnnotation(ReadOnlyViewAnnotation)?.Value as string;
            if (sourceName is null
                || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            throw new ReadOnlyViewWriteAttemptException(
                entry.Metadata.DisplayName(),
                sourceName,
                entry.State);
        }
    }
}

/// <summary>
/// Base context behavior for consumer services that query external read-only views.
/// It keeps normal reads untracked and installs the save-changes guard.
/// </summary>
public abstract class ReadOnlyViewsDbContext : DbContext
{
    /// <summary>
    /// Initializes a consumer context.
    /// </summary>
    /// <param name="options">Context configuration.</param>
    protected ReadOnlyViewsDbContext(DbContextOptions options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(ReadOnlyViewSaveChangesInterceptor.Instance);
    }
}
