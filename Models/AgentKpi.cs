namespace TicketSystem.Models;

//обобщени KPI показатели за един агент
public sealed class AgentKpi
{
    public int AgentId { get; set; }

    public string AgentName { get; set; } = string.Empty;

    public int TotalResolved { get; set; }

    public int ActiveTickets { get; set; }

    public double? AverageRating { get; set; }

    public int RatingCount { get; set; }

    public double? AverageResolutionMinutes { get; set; }

    public double? SlaComplianceRate { get; set; }
}
