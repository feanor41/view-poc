using Microsoft.EntityFrameworkCore;
using Vwp.Accounts.Api.Data;
using Vwp.Accounts.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");

builder.Services.AddDbContext<AccountsDbContext>(options => options.UseSqlServer(connectionString));

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (AccountsDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPut("/accounts/{accountId:guid}", async (
    Guid accountId,
    AccountInput input,
    AccountsDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(input.Name))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(input.Name)] = ["Name is required."]
        });
    }

    var account = await db.Accounts.SingleOrDefaultAsync(
        value => value.Id == accountId,
        cancellationToken);
    if (account is null)
    {
        account = new Account
        {
            Id = accountId,
            Name = input.Name.Trim(),
            Status = string.IsNullOrWhiteSpace(input.Status) ? "Active" : input.Status.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Accounts.Add(account);
    }
    else
    {
        account.Name = input.Name.Trim();
        account.Status = string.IsNullOrWhiteSpace(input.Status) ? "Active" : input.Status.Trim();
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AccountResponse(account.Id, account.Name, account.Status, account.CreatedAt));
});

app.MapPut("/accounts/{accountId:guid}/assets/{assetId:guid}", async (
    Guid accountId,
    Guid assetId,
    AssetInput input,
    AccountsDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Kind))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(input.Name)] = ["Name is required."],
            [nameof(input.Kind)] = ["Kind is required."]
        });
    }

    if (!await db.Accounts.AnyAsync(value => value.Id == accountId, cancellationToken))
    {
        return Results.NotFound(new { message = $"Account '{accountId}' was not found." });
    }

    var asset = await db.Assets.SingleOrDefaultAsync(
        value => value.Id == assetId,
        cancellationToken);
    if (asset is null)
    {
        asset = new Asset { Id = assetId, AccountId = accountId };
        db.Assets.Add(asset);
    }
    else if (asset.AccountId != accountId)
    {
        return Results.Conflict(new { message = "The asset belongs to a different account." });
    }

    asset.Name = input.Name.Trim();
    asset.Kind = input.Kind.Trim();
    asset.Value = input.Value;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AssetResponse(asset.Id, asset.AccountId, asset.Name, asset.Kind, asset.Value));
});

app.MapPut("/accounts/{accountId:guid}/contact-information", async (
    Guid accountId,
    ContactInformationInput input,
    AccountsDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrWhiteSpace(input.Phone))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(input.Email)] = ["Email is required."],
            [nameof(input.Phone)] = ["Phone is required."]
        });
    }

    if (!await db.Accounts.AnyAsync(value => value.Id == accountId, cancellationToken))
    {
        return Results.NotFound(new { message = $"Account '{accountId}' was not found." });
    }

    var contact = await db.ContactInformation.SingleOrDefaultAsync(
        value => value.AccountId == accountId,
        cancellationToken);
    if (contact is null)
    {
        contact = new ContactInformation { Id = Guid.NewGuid(), AccountId = accountId };
        db.ContactInformation.Add(contact);
    }

    contact.Email = input.Email.Trim();
    contact.Phone = input.Phone.Trim();
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new ContactInformationResponse(contact.Id, contact.AccountId, contact.Email, contact.Phone));
});

app.MapGet("/accounts/{accountId:guid}", async (
    Guid accountId,
    AccountsDbContext db,
    CancellationToken cancellationToken) =>
{
    var account = await db.Accounts
        .AsNoTrackingWithIdentityResolution()
        .Include(value => value.Assets)
        .Include(value => value.ContactInformation)
        .SingleOrDefaultAsync(value => value.Id == accountId, cancellationToken);

    return account is null
        ? Results.NotFound()
        : Results.Ok(new AccountDetailsResponse(
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

app.Run();

internal sealed record AccountInput(string Name, string? Status);
internal sealed record AssetInput(string Name, string Kind, decimal Value);
internal sealed record ContactInformationInput(string Email, string Phone);
internal sealed record AccountResponse(Guid Id, string Name, string Status, DateTimeOffset CreatedAt);
internal sealed record AssetResponse(Guid Id, Guid AccountId, string Name, string Kind, decimal Value);
internal sealed record ContactInformationResponse(Guid Id, Guid AccountId, string Email, string Phone);
internal sealed record AccountDetailsResponse(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<AssetResponse> Assets,
    ContactInformationResponse? ContactInformation);
