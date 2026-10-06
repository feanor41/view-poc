namespace Vwp.Cases.Api.Domain;

// These consumer-owned CLR types deliberately mirror Accounts' read model locally.
public sealed class Account
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
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

public sealed class ContactInformation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public Account? Account { get; set; }
}

public sealed class Case
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid CaseTypeId { get; set; }
    public Guid CaseGroupId { get; set; }
    public string Title { get; set; } = string.Empty;
    public Account? Account { get; set; }
    public Asset? Asset { get; set; }
    public CaseType? CaseType { get; set; }
    public CaseGroup? CaseGroup { get; set; }
}

public sealed class CaseType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<Case> Cases { get; set; } = new List<Case>();
}

public sealed class CaseGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<Case> Cases { get; set; } = new List<Case>();
}
