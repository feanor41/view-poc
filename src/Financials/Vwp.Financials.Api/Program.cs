using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Vwp.Financials.Api.Data;
using Vwp.Financials.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");

builder.Services.AddDbContext<FinancialsDbContext>(options => options.UseSqlServer(connectionString));

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (FinancialsDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/accounts/{accountId:guid}", async (
    Guid accountId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    var account = await db.Accounts
        .AsNoTrackingWithIdentityResolution()
        .Include(value => value.Assets)
        .SingleOrDefaultAsync(value => value.Id == accountId, cancellationToken);
    if (account is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new AccountResponse(
        account.Id,
        account.Name,
        account.Status,
        account.CreatedAt,
        account.Assets
            .OrderBy(asset => asset.Name)
            .Select(asset => new AssetResponse(asset.Id, asset.AccountId, asset.Name, asset.Kind, asset.Value))
            .ToArray()));
});

app.MapPost("/operations", async (
    OperationInput input,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 500)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(input.Description)] = ["Description is required and must be at most 500 characters."]
        });
    }

    // A fresh query reads the consumer's local view. Keep the imported Account
    // untracked and assign only AccountId so Add cannot attach the read-only graph.
    var account = await db.Accounts
        .AsNoTracking()
        .SingleOrDefaultAsync(value => value.Id == input.AccountId, cancellationToken);
    if (account is null)
    {
        return Results.NotFound(new { message = $"Account '{input.AccountId}' was not found." });
    }

    var operation = new Operation
    {
        Id = Guid.NewGuid(),
        AccountId = account.Id,
        Description = input.Description.Trim(),
        Amount = input.Amount
    };
    db.Operations.Add(operation);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created(
        $"/operations/{operation.Id}",
        new OperationResponse(operation.Id, operation.AccountId, operation.Description, operation.Amount));
});

app.MapGet("/operations/{operationId:guid}", async (
    Guid operationId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    var operation = await db.Operations
        .AsNoTracking()
        .SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken);
    return operation is null
        ? Results.NotFound()
        : Results.Ok(new OperationResponse(
            operation.Id, operation.AccountId, operation.Description, operation.Amount));
});

app.MapPost("/accounts/{accountId:guid}/write-probe/{operation}", async (
    Guid accountId,
    string operation,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    var account = await db.Accounts
        .AsTracking()
        .SingleOrDefaultAsync(value => value.Id == accountId, cancellationToken);
    if (account is null)
    {
        return Results.NotFound();
    }

    switch (operation.ToLowerInvariant())
    {
        case "insert":
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Name = "forbidden consumer insert",
                Status = "Probe",
                CreatedAt = DateTimeOffset.UtcNow
            });
            break;
        case "update":
            account.Name = $"{account.Name} (forbidden consumer update)";
            db.Accounts.Update(account);
            break;
        case "delete":
            db.Accounts.Remove(account);
            break;
        default:
            return Results.BadRequest(new { message = "Use insert, update, or delete." });
    }

    try
    {
        await db.SaveChangesAsync(cancellationToken);
        return Results.Problem("The view-backed write unexpectedly succeeded.", statusCode: 500);
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not mapped to a table", StringComparison.Ordinal))
    {
        return Results.Conflict(new { blocked = true, guard = "ef-view-mapping", operation });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/execute-update", async (
    Guid accountId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    try
    {
        var affected = await db.Accounts
            .Where(value => value.Id == accountId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(value => value.Name, value => value.Name + " (forbidden bulk update)"),
                cancellationToken);
        return affected == 0
            ? Results.NotFound()
            : Results.Problem("The view-only bulk update unexpectedly changed a source row.", statusCode: 500);
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not mapped to a table", StringComparison.Ordinal))
    {
        return Results.Conflict(new { blocked = true, guard = "ef-view-mapping", operation = "execute-update" });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/execute-delete", async (
    Guid accountId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    try
    {
        var affected = await db.Accounts
            .Where(value => value.Id == accountId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected == 0
            ? Results.NotFound()
            : Results.Problem("The view-only bulk delete unexpectedly changed a source row.", statusCode: 500);
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not mapped to a table", StringComparison.Ordinal))
    {
        return Results.Conflict(new { blocked = true, guard = "ef-view-mapping", operation = "execute-delete" });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/raw-update-view", async (
    Guid accountId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    try
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [dbo].[Accounts] SET [Name] = [Name] + N' (forbidden raw view update)' WHERE [Id] = {accountId}",
            cancellationToken);
        return affected == 0
            ? Results.NotFound()
            : Results.Problem("Raw SQL unexpectedly changed a row through the consumer view.", statusCode: 500);
    }
    catch (SqlException exception) when (exception.Number == 229)
    {
        return Results.Conflict(new { blocked = true, guard = "sql-permissions", operation = "raw-update-view" });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/raw-update-source", async (
    Guid accountId,
    FinancialsDbContext db,
    CancellationToken cancellationToken) =>
{
    try
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Accounts].[dbo].[Accounts] SET [Name] = [Name] + N' (forbidden raw source update)' WHERE [Id] = {accountId}",
            cancellationToken);
        return affected == 0
            ? Results.NotFound()
            : Results.Problem("Raw SQL unexpectedly changed an Accounts source row.", statusCode: 500);
    }
    catch (SqlException exception) when (exception.Number == 229)
    {
        return Results.Conflict(new { blocked = true, guard = "sql-permissions", operation = "raw-update-source" });
    }
});

app.Run();

internal sealed record AccountResponse(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<AssetResponse> Assets);
internal sealed record AssetResponse(Guid Id, Guid AccountId, string Name, string Kind, decimal Value);

internal sealed record OperationInput(Guid AccountId, string Description, decimal Amount);
internal sealed record OperationResponse(Guid Id, Guid AccountId, string Description, decimal Amount);
