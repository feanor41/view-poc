# Cross-database views and EF Core mapping

## PoC question

Can a service database expose rows owned by Accounts through a local SQL Server view and let a service-local EF Core model query them as a read-only graph, without copying the rows or synchronizing them?

For a single SQL Server instance, the answer for ordinary views is yes. Each consumer database can contain a local view with an explicit projection such as:

```sql
CREATE VIEW dbo.Accounts AS
SELECT Id, Name, Status, CreatedAt
FROM [Accounts].[dbo].[Accounts];
```

The SQL view resides in the consumer database. The three-part source name resolves against another database on the same instance. The view does not copy data; a later query reads the source rows subject to SQL Server's transaction isolation rules. With ordinary read-committed queries, a query started after the source transaction commits observes the committed update. A query running in an existing snapshot can continue to see its earlier snapshot.

The consumer views use explicit column lists. Changes to a source schema must be coordinated with the view and consumer clone. Because these views are not schema-bound, SQL Server may require sp_refreshview after source metadata changes.

## Service-local EF Core models

Each consumer declares its own Account, Asset, and/or ContactInformation CLR types with a stable key and the same column shape as the projected view. The types live in separate service assemblies and do not reference the Accounts assembly.

```csharp
modelBuilder.Entity<Account>(entity =>
{
    entity.HasKey(account => account.Id);
    entity.ToExternalReadOnlyView("Accounts");

    entity.HasMany(account => account.Assets)
        .WithOne(asset => asset.Account)
        .HasForeignKey(asset => asset.AccountId);
});
```

`ToExternalReadOnlyView(viewName, viewSchema = "dbo")` maps queries to the consumer-local view and marks the entity for read-only model validation. The source database and table are expressed only in the SQL view definition. Keys and relationships remain configured, so EF can materialize an Account and its view-backed related rows. Every imported entity is mapped explicitly. The SQL databases do not define foreign keys to objects in another database; those relationships exist only in the consumer EF model.

The mapping extension does not create the SQL view. EF Core's ToView assumes the view already exists and does not create it in a migration. Here, database/init.sql owns the initial SQL schema, permissions, and view definitions.

## Native EF Core rejection and model validation

In the pinned EF Core 10.0.12 SQL Server provider, a keyed entity mapped **only** with `ToView` has no table mapping. EF Core therefore rejects `SaveChanges` for Added, Modified, or Deleted entries with a native `InvalidOperationException` before issuing DML. Calling `Add`, `Update`, `Attach`, or `Remove` and assigning CLR properties remain possible; rejection happens when persistence is attempted. Attaching an unchanged entity and saving is valid and writes nothing. This behavior covers synchronous and asynchronous saves.

`ToView` removes the conventional table mapping, but EF Core permits an explicit `ToTable` mapping alongside it. Such a model queries the view and writes the table. `ReadOnlyViewsDbContext` registers a public `IModelFinalizingConvention` to reject that accidental configuration after `OnModelCreating` has finished. For each marked entity, it requires a key and view and rejects a table mapping, table mapping fragments, or insert/update/delete stored-procedure mappings. A conflicting mapping produces an actionable model-build error. Consumer-owned entities are unmarked and keep their normal writable mappings.

The shared library has no `SaveChanges` interceptor or `Update` override. Native EF Core enforces persistence rejection. Use the extension together with `ReadOnlyViewsDbContext` to obtain model validation; a context overriding `ConfigureConventions` must call its base implementation. The current proof of concept uses ordinary keyed entities; it does not claim general support for arbitrary inheritance, owned-object, or split-mapping graphs.

`ExecuteUpdate` and `ExecuteDelete` bypass `SaveChanges`, but this view-only model is rejected during their translation because there is no writable table target. Raw SQL bypasses entity mappings entirely. Each consumer SQL login retains SELECT on its imported views and necessary source tables, with INSERT, UPDATE, and DELETE denied on both. A simple SQL view can otherwise be updatable. Those permissions provide the database boundary even if a caller uses raw SQL or another mapping.

The consumer probes exercise `Add`, `Update`, and `Remove` followed by `SaveChangesAsync`, both bulk operations, and parameterized raw UPDATE against the consumer view and three-part Accounts table. `scripts/verify-local.py` requires native `ef-view-mapping` rejection for mapped writes and `sql-permissions` rejection for raw writes, then reads back unchanged data. Probe classification recognizes the pinned provider's native diagnostic text; a wording change can fail verification and requires review. The diagnostic handler does not enforce read-only behavior or suppress unrelated exceptions.

Consumer queries use `AsNoTrackingWithIdentityResolution` for graph reads. This avoids retaining stale values in a long-lived change tracker; it is not the write protection. `HasNoKey` would prevent Account from acting as a relationship principal or navigation target, so it cannot preserve this graph. Scalar `PropertySaveBehavior` settings also do not replace entity-level protection because they do not block deletes or raw SQL.

## Writable consumer-owned entities

Financials still maps `Operation` to its own table. Its create endpoint validates the account through the local Account view, assigns only `AccountId`, and adds the new Operation. It does not attach or add the Account navigation graph. This matters because `Add` traverses reachable entities and could otherwise mark a detached existing Account as Added, causing native view-only rejection. The repeatable verifier creates and reads back an Operation and checks that an unknown Account is rejected.

## Cross-database authorization and ownership

The SQL setup creates distinct logins for Accounts and each consumer. Consumer logins receive SELECT on only their local views and the source tables those views need. They receive no DML on Accounts data. The PoC leaves TRUSTWORTHY and database-level chaining off and does not enable server-wide cross-database ownership chaining.

Granting source SELECT to a consumer login means that login can also issue a direct read against those source objects. The PoC's security objective is to prevent consumer mutation and synchronization, not to hide the source from read-capable SQL principals. A stricter object-hiding design would need a separately evaluated permission-signing or ownership strategy.

## Indexed (materialized) views

SQL Server's materialized-view equivalent is an indexed view. SQL Server requires indexed views to use SCHEMABINDING, and the view can reference only base tables in the same database. It also requires a unique clustered index first, plus deterministic expressions and specific session SET options.

That rules out putting an indexed view in Financials, Cases, or Notifications that directly references [Accounts] tables. A view in Accounts could be indexed over Accounts-owned tables, but it is maintained during writes and moves the materialization cost to the owning service. A consumer could then read that owner-side object through an ordinary cross-database view; that remains an ordinary consumer view and is not a cross-database indexed view.

The runtime PoC therefore uses ordinary views. Indexed-view behavior and performance would need a separate benchmark with representative write rates and query shapes before being considered.

## Primary documentation

- [CREATE VIEW (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-view-transact-sql?view=sql-server-ver17)
- [Create indexed views](https://learn.microsoft.com/en-us/sql/relational-databases/views/create-indexed-views?view=sql-server-ver17)
- [EF Core entity types and view mapping](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types#view-mapping)
- [EF Core 10.0.12 native read-only persistence rejection](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Relational/Update/Internal/CommandBatchPreparer.cs)
- [Public model-finalizing conventions](https://learn.microsoft.com/en-us/ef/core/modeling/bulk-configuration#conventions)
- [Explicit tracking and entity graphs](https://learn.microsoft.com/en-us/ef/core/change-tracking/explicit-tracking)
- [Keyless entity types](https://learn.microsoft.com/en-us/ef/core/modeling/keyless-entity-types)
- [Tracking vs. no-tracking queries](https://learn.microsoft.com/en-us/ef/core/querying/tracking)
- [ExecuteUpdate and ExecuteDelete](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)
- [SQL Server foreign-key limitations](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#foreign-key-constraints)
- [Cross-database ownership chaining](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/cross-db-ownership-chaining-server-configuration-option?view=sql-server-ver17)
- [SQL Server transaction isolation](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver17)
