# Service-Owned Read Models over Cross-Database Views

## The idea

A service owns the data it writes. Another service can keep its own local EF Core model for the fields it needs while querying those fields through a SQL view in its own database. The view selects from the owning service's tables; it does not create a second copy or require a synchronization process.

We call this a **service-owned read model over a cross-database view**. The name describes the boundaries: ownership stays with the source service, the consumer keeps its own typed model, and a local view connects that model to selected source data. In this article, cross-database means databases on the same SQL Server instance. It does not mean a distributed query across independent servers.

~~~mermaid
flowchart LR
    OwnerAPI["Accounts service"] -->|"EF Core writes"| OwnerDB
    subgraph SQL["One SQL Server instance"]
        OwnerDB["Accounts database<br/>canonical Accounts and Assets tables"]
        ConsumerDB["Financials database<br/>local Accounts view and owned Operations table"]
        OwnerDB -->|"three-part-name SELECT; no row copy"| ConsumerDB
    end
    ConsumerAPI["Financials service"] -->|"EF Core reads local view"| ConsumerDB
    ConsumerAPI -->|"writes owned Operation"| ConsumerDB
~~~

The view is a database object in the consumer database. Its query resolves the source with a three-part name such as Accounts.dbo.Accounts. SQL Server resolves that name within the current instance. The consumer type remains a CLR type owned by the consumer service; it does not need to reference the source service's entity assembly.

## One request path

The VWP proof of concept demonstrates the pattern with an Account and a Financials Operation:

1. Accounts receives a request and commits an Account row to Accounts.dbo.Accounts.
2. Financials queries its own Financials.dbo.Accounts view. A new read after the source transaction commits can see the row without polling, a message, or a copied table.
3. The Financials API validates the AccountId through a fresh no-tracking query of that view.
4. Financials writes an Operation to its own Financials.dbo.Operations table, storing the AccountId as a scalar reference. A later request can read the Operation back.

The view provides current source data for each query according to SQL Server transaction isolation. Under the usual read-committed request path, a query started after commit sees the committed source row. A query already running in a snapshot can continue to see its earlier version. “No synchronization delay” therefore means there is no separate copy pipeline to wait for; it does not override transaction isolation or make two service writes atomic.

VWP applies the same boundary to these read sets:

| Source owner | Consumer database | Local view-backed model |
| --- | --- | --- |
| Accounts | Financials | Account, Asset |
| Accounts | Cases | Account, Asset, ContactInformation |
| Accounts | Notifications | Account, ContactInformation |

Each consumer also owns its own business tables. For example, the Financials Operation remains a writable Financials entity even though its Account reference is read through a view.

## Mapping the view in EF Core

Keep the consumer model local to the service. Configure a stable key and the relationships that the consumer needs, then map the entity to the view in the consumer database. The VWP helper is used like this:

~~~csharp
modelBuilder.Entity<Account>(entity =>
{
    entity.HasKey(account => account.Id);
    entity.ToExternalReadOnlyView("Accounts");

    entity.HasMany(account => account.Assets)
        .WithOne(asset => asset.Account)
        .HasForeignKey(asset => asset.AccountId);
});
~~~

EF Core's `ToView` mapping assumes the view already exists; it does not create the SQL view in a migration. Assign the view definition to the database bootstrap or migration path for the consumer database. In VWP, [`database/init.sql`](../../database/init.sql) provisions the views, tables, and permissions.

The keys and navigations allow EF Core to materialize and relate the consumer's read graph. These are model relationships, not cross-database foreign keys. A local view does not add a physical constraint to the source or consumer tables.

In the pinned EF Core 10.0.12 SQL Server provider, an entity mapped only with **ToView** has no table write target. A changed Added, Modified, or Deleted entity is rejected when SaveChanges prepares persistence commands. **ExecuteUpdate** and **ExecuteDelete** are rejected during query translation when there is no writable table mapping. Calling Add, Update, Attach, or Remove can still change the change tracker; the SaveChanges rejection is the persistence boundary. An unchanged attached entity has no write to perform.

VWP registers a public model-finalizing convention through ReadOnlyViewsDbContext. It checks entities marked by the helper after model configuration finishes, requires a key and view, and rejects a table mapping, table mapping fragment, or insert/update/delete stored-procedure mapping. That catches an accidental writable mapping even if another configuration call was added later. A context that overrides ConfigureConventions must call the base implementation for this check to run.

No-tracking queries help avoid stale values retained by a long-lived context, but no-tracking is not the write guard. The VWP model keeps keys and ordinary navigations rather than changing the shared clones into keyless query types. This implementation is specific to the tested EF Core/provider version and the ordinary keyed graphs in the PoC; inheritance, owned objects, and split-table arrangements need separate validation.

## EF behavior and database permissions are separate layers

EF's mapping controls writes issued through that mapping. Raw SQL does not use the entity mapping, so each consumer SQL login also needs database-enforced permissions:

- Grant SELECT on the consumer's local view and the source objects needed by that view.
- Explicitly deny INSERT, UPDATE, and DELETE on the imported views and the source objects for that consumer login.
- Grant write access only to the consumer's own physical tables.

This makes the database permission boundary apply even if a code path bypasses EF's mapped SaveChanges behavior. In VWP, consumer logins have SELECT on the local views and the source objects they use, with DML denied on both; database ownership chaining stays off. A login with SELECT on an Accounts table can also read that table directly, so the PoC prevents mutation but does not conceal source objects from read-authorized principals. Review authorization separately if consumers must be limited to the projected columns. More restrictive access needs its own permission-signing or ownership design; do not enable cross-database ownership chaining broadly without a security review.

The VWP example exercises mapped SaveChanges writes, both bulk update/delete APIs, and parameterized raw UPDATE probes against a view and a source object. The probes document the expected boundary; they do not replace least-privilege SQL grants and denials.

## When the pattern fits

This pattern is a candidate when services already use databases on one SQL Server instance, consumers need a current read of a small selected portion of another service's data, and the source owner remains responsible for writes and schema changes.

| Prefer a view-backed read model when… | Consider another integration when… |
| --- | --- |
| The databases share a SQL Server instance and can resolve three-part source names. | The source and consumer must run on independent SQL Server instances or different database products. |
| A consumer needs the current committed source projection during a request. | A consumer must keep working while the source server is unavailable, or needs independent read scaling. |
| The consumer can accept a local view and model contract that changes with the source projection. | The consumer needs durable historical snapshots, strong cross-service referential guarantees, or independent versioning of its data. |
| The consumer is permitted to read the selected source columns. | The source data must be hidden from the consumer's SQL principal, including direct SELECT. |

Compared with a copied or event-projected read model, the view avoids replicated rows and a delivery/synchronization pipeline. It also makes consumer queries depend on the source database at read time, shares database capacity, and couples deployments to the projected column contract. An event-driven projection or explicit API call can provide stronger isolation or a narrower access surface, at the cost of additional infrastructure, latency, or request coupling. Choose based on those operational requirements rather than treating one approach as universally superior.

## Boundaries and operational costs

- **Server placement:** the SQL Server views in this PoC use three-part names and all databases live on one instance. Moving a database to another server is an architecture change, not a connection-string-only change.
- **Availability and load:** a consumer view query depends on the source database and competes for its query resources. Watch query plans, indexes, and source load as more consumers use the projection.
- **Referential integrity:** SQL Server foreign keys do not cross these database boundaries. The Financials request checks Account existence before it writes, but an Account can be deleted after that check. This is a point-in-time validation, not a remote FK or distributed transaction.
- **Schema coordination:** define view columns explicitly and treat them as a versioned contract between the owner and consumers. Coordinate source changes with view definitions and consumer EF mappings; SQL Server may require refreshing view metadata after source schema changes.
- **Security visibility:** grant only selected reads and deny all consumer DML on imported data. Because the SQL login is also granted SELECT on required source objects, assess whether direct source reads are acceptable.
- **Indexed views:** SQL Server indexed views require schema binding and can reference base tables only in the same database. They cannot directly materialize these cross-database consumer views. An owner-side indexed view may be exposed through an ordinary consumer view, but the owner pays its write-maintenance cost.
- **Evidence:** VWP's recorded 3,510-flow performance run is a local single-node characterization from before the VWP-6 mapping change. It is useful as a reproducible starting point, not a production capacity or scaling guarantee.

## Adoption checklist

For each proposed source/consumer pair, answer these questions before adding the view:

1. **Ownership:** Which service is the single writer for each column, and which consumer read scenarios require the projection?
2. **Placement:** Will both databases remain on the same SQL Server instance for the required lifetime?
3. **Projection:** Which columns are necessary? Define a consumer-local view with an explicit column list and coordinate schema versions.
4. **Consumer model:** Add a service-local CLR model with the required key and navigation shape; map it to the local view and validate the finalized model has no table or write stored-procedure mapping.
5. **Permissions:** Give the consumer login only the SELECT rights it needs, deny DML on the imported view and source objects, and keep write grants on consumer-owned tables.
6. **Consistency:** Decide which isolation level and stale-read window the business operation accepts. Treat existence checks as point-in-time unless you add a separate durable invariant.
7. **Failure and load:** Measure query cost on the owner, plan for source unavailability, and decide whether the consumer requires an independent copy or cache.
8. **Verification:** Test fresh visibility after a committed source write, consumer SaveChanges, bulk writes, raw SQL permissions, schema evolution, and all owned-table writes.
9. **Alternative:** If same-instance coupling, source read access, or shared availability is unacceptable, compare an API contract or an asynchronously maintained consumer projection.

## References and VWP details

- [VWP architecture and ownership map](../architecture.md)
- [EF Core mapping and SQL Server constraints in VWP](../ef-core-cross-database-views.md)
- [Local Kubernetes setup and verification](../local-kubernetes.md)
- [Historical performance results and reproduction steps](../performance-baseline.md)
- [SQL Server CREATE VIEW](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-view-transact-sql?view=sql-server-ver17)
- [SQL Server multipart object names](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/transact-sql-syntax-conventions-transact-sql?view=sql-server-ver17)
- [SQL Server indexed views](https://learn.microsoft.com/en-us/sql/relational-databases/views/create-indexed-views?view=sql-server-ver17)
- [EF Core view mapping](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types#view-mapping)
- [EF Core 10.0.12 native read-only persistence rejection](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Relational/Update/Internal/CommandBatchPreparer.cs)
- [EF Core ExecuteUpdate and ExecuteDelete](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)
- [SQL Server transaction isolation](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver17)
- [SQL Server cross-database ownership chaining](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/cross-db-ownership-chaining-server-configuration-option?view=sql-server-ver17)
