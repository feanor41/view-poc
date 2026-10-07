using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Vwp.DataAccess;

internal sealed class ReadOnlyViewConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var isReadOnly = entityType.GetAllBaseTypesInclusive().Any(type =>
                type.FindAnnotation(ExternalReadOnlyViewExtensions.ReadOnlyViewAnnotation)?.Value is true);
            if (!isReadOnly)
            {
                continue;
            }

            if (entityType.FindPrimaryKey() is null || entityType.GetViewName() is null)
            {
                throw new InvalidOperationException(
                    $"Read-only entity '{entityType.DisplayName()}' must retain a key and a ToView mapping.");
            }

            if (entityType.GetTableName() is not null
                || entityType.GetMappingFragments(StoreObjectType.Table).Any()
                || entityType.GetInsertStoredProcedure() is not null
                || entityType.GetUpdateStoredProcedure() is not null
                || entityType.GetDeleteStoredProcedure() is not null)
            {
                throw new InvalidOperationException(
                    $"Read-only entity '{entityType.DisplayName()}' has a writable table or stored-procedure mapping. "
                    + "Keep its ToView mapping and remove ToTable, table splitting, and write stored-procedure mappings.");
            }
        }
    }
}
