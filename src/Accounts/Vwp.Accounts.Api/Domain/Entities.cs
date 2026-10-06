namespace Vwp.Accounts.Api.Domain;

public sealed class Account
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
    public ICollection<AccountHistory> History { get; set; } = new List<AccountHistory>();
    public ContactInformation? ContactInformation { get; set; }
}

public sealed class Asset
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public Account? Account { get; set; }
}

public sealed class AccountHistory
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public Account? Account { get; set; }
}

public sealed class ContactInformation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public Account? Account { get; set; }
}
