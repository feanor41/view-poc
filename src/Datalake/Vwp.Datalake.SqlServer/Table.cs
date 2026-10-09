namespace Vwp.Datalake.SqlServer;

internal sealed record Table(string Name, bool AccountsSource, string Columns)
{
    internal string[] NullableColumns { get; init; } = [];
    internal string Identifier => Settings.Quote(Name);
    internal string ColumnNames => string.Join(",", Fields.Select(columnField => Settings.Quote(columnField.Name)));
    internal (string Name, string Type)[] Fields => Columns.Split('|').Select(column =>
    {
        var separator = column.IndexOf(' ');
        return (column[..separator], column[(separator + 1)..]);
    }).ToArray();
    internal string Definition => string.Join(",", Fields.Select(columnField => $"{Settings.Quote(columnField.Name)} {columnField.Type} {(NullableColumns.Contains(columnField.Name) ? "NULL" : "NOT NULL")}"));
    internal string JsonDefinition => string.Join(",", Fields.Select(columnField => $"{Settings.Quote(columnField.Name)} {columnField.Type} '$.{columnField.Name}'"));
    internal string Updates => string.Join(",", Fields.Where(columnField => columnField.Name != "Id").Select(columnField => $"T.{Settings.Quote(columnField.Name)}=J.{Settings.Quote(columnField.Name)}"));
    internal string ExpectedColumnsSql => string.Join(",", Fields.Select(columnField =>
    {
        var type = columnField.Type;
        var baseType = type.Split('(')[0];
        var details = type.Contains('(') ? type[(type.IndexOf('(') + 1)..^1].Split(',').Select(int.Parse).ToArray() : [];
        var (length, precision, scale) = baseType switch
        {
            "uniqueidentifier" => (16, 0, 0),
            "bit" => (1, 1, 0),
            "nvarchar" => (details[0] * 2, 0, 0),
            "decimal" => (9, details[0], details[1]),
            "datetimeoffset" => (10, 34, details[0]),
            "datetime2" => (8, 27, details[0]),
            _ => throw new InvalidOperationException("Unsupported fixed schema contract type.")
        };
        return $"(N'{columnField.Name}',N'{baseType}',{length},{precision},{scale},{(NullableColumns.Contains(columnField.Name) ? 1 : 0)})";
    }));

    internal static readonly Table Reporting = new("AccountOperations", false,
        "Id uniqueidentifier|AccountId uniqueidentifier|AccountName nvarchar(200)|AccountStatus nvarchar(40)|AccountMissing bit|Description nvarchar(500)|Amount decimal(18,2)|SourceAccountCreatedAt datetimeoffset(7)|ReportedAt datetime2(7)")
    { NullableColumns = ["AccountName", "AccountStatus", "SourceAccountCreatedAt"] };

    internal static readonly Table[] All =
    [
        new("Accounts", true, "Id uniqueidentifier|Name nvarchar(200)|Status nvarchar(40)|CreatedAt datetimeoffset(7)"),
        new("Assets", true, "Id uniqueidentifier|AccountId uniqueidentifier|Name nvarchar(200)|Kind nvarchar(80)|Value decimal(18,2)"),
        new("ContactInformation", true, "Id uniqueidentifier|AccountId uniqueidentifier|Email nvarchar(320)|Phone nvarchar(50)"),
        new("AccountHistory", true, "Id uniqueidentifier|AccountId uniqueidentifier|Description nvarchar(1000)|OccurredAt datetimeoffset(7)"),
        new("PaymentMeans", false, "Id uniqueidentifier|Name nvarchar(100)"),
        new("Operations", false, "Id uniqueidentifier|AccountId uniqueidentifier|Description nvarchar(500)|Amount decimal(18,2)"),
        new("Transactions", false, "Id uniqueidentifier|OperationId uniqueidentifier|PaymentMeansId uniqueidentifier|Amount decimal(18,2)"),
        new("CollectionOrders", false, "Id uniqueidentifier|OperationId uniqueidentifier|DueAt datetimeoffset(7)")
    ];
}
