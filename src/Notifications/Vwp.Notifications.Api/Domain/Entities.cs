namespace Vwp.Notifications.Api.Domain;

// These consumer-owned CLR types deliberately mirror Accounts' read model locally.
public sealed class Account
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ContactInformation? ContactInformation { get; set; }
}

public sealed class ContactInformation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public Account? Account { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid NotificationTypeId { get; set; }
    public string Message { get; set; } = string.Empty;
    public Account? Account { get; set; }
    public NotificationType? NotificationType { get; set; }
}

public sealed class NotificationType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
