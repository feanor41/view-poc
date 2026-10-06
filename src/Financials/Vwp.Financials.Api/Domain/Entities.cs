namespace Vwp.Financials.Api.Domain;

// These are Financials-owned CLR types. Account and Asset intentionally mirror
// the Accounts contract locally so Financials has no assembly dependency on it.
public sealed class Account
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
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

public sealed class Operation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Account? Account { get; set; }
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public ICollection<CollectionOrder> CollectionOrders { get; set; } = new List<CollectionOrder>();
}

public sealed class Transaction
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid PaymentMeanId { get; set; }
    public decimal Amount { get; set; }
    public Operation? Operation { get; set; }
    public PaymentMean? PaymentMean { get; set; }
}

public sealed class PaymentMean
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}

public sealed class CollectionOrder
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public Operation? Operation { get; set; }
}
