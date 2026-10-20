namespace TicketSystem.Models;

//тук се пазят обратни нотификации
public sealed class TicketNotification
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int TicketId { get; set; }

    public string Message { get; set; } = string.Empty;

    public string Type { get; set; } = "OpenTicket";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsRead { get; set; }
}
