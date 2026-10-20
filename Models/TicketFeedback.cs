namespace TicketSystem.Models;

//тук се пази обратна връзка
public sealed class TicketFeedback
{
    public int UserId { get; set; }

    public int Rating { get; set; }

    public string Comment { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
