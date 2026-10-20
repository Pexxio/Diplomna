namespace TicketSystem.Models;

//ред с реални данни за един решен (затворен) тикет, за KPI таблицата и Excel износа
public sealed class ResolvedTicketRow
{
    public string Number { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public string AgentName { get; set; } = string.Empty;

    public string RequesterName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ClosedAtUtc { get; set; }

    public int ResolutionMinutes { get; set; }

    public int? Rating { get; set; }

    public bool MetSla { get; set; }
}
