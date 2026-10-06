using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Vwp.Cases.Api.Data;
using Vwp.Cases.Api.Domain;
using Vwp.DataAccess;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");

builder.Services.AddDbContext<CasesDbContext>(options => options.UseSqlServer(connectionString));

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CasesDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/accounts/{accountId:guid}", async (
    Guid accountId,
    CasesDbContext db,
    CancellationToken cancellationToken) =>
{
    var account = await db.Accounts
        .AsNoTrackingWithIdentityResolution()
        .Include(value => value.Assets)
        .Include(value => value.ContactInformation)
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
            .ToArray(),
        account.ContactInformation is null
            ? null
            : new ContactInformationResponse(
                account.ContactInformation.Id,
                account.ContactInformation.AccountId,
                account.ContactInformation.Email,
                account.ContactInformation.Phone)));
});

app.MapPost("/accounts/{accountId:guid}/write-probe/{operation}", async (
    Guid accountId,
    string operation,
    CasesDbContext db,
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
    catch (ReadOnlyViewWriteAttemptException)
    {
        return Results.Conflict(new { blocked = true, guard = "ef-save-changes", operation });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/execute-update", async (
    Guid accountId,
    CasesDbContext db,
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
            : Results.Problem("The database-permission probe unexpectedly changed a source row.", statusCode: 500);
    }
    catch (SqlException exception) when (exception.Number == 229)
    {
        return Results.Conflict(new { blocked = true, guard = "sql-permissions", operation = "execute-update" });
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not mapped to a table", StringComparison.Ordinal))
    {
        return Results.Conflict(new { blocked = true, guard = "ef-view-mapping", operation = "execute-update" });
    }
});

app.MapPost("/accounts/{accountId:guid}/write-probe/raw-update-view", async (
    Guid accountId,
    CasesDbContext db,
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
    CasesDbContext db,
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
    IReadOnlyCollection<AssetResponse> Assets,
    ContactInformationResponse? ContactInformation);
internal sealed record AssetResponse(Guid Id, Guid AccountId, string Name, string Kind, decimal Value);
internal sealed record ContactInformationResponse(Guid Id, Guid AccountId, string Email, string Phone);
