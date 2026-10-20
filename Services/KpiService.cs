using ClosedXML.Excel;
using TicketSystem.Models;

namespace TicketSystem.Services;

//изчислява реални KPI показатели по агент от съществуващите тикети и генерира Excel износ
public sealed class KpiService
{
    //агентът, на когото се записва заслугата за решаването - този, който е затворил тикета,
    //а ако липсва (стари данни) - назначеният агент
    private static int? GetCreditedAgentId(Ticket ticket)
    {
        return ticket.ClosedByAgentId ?? ticket.AssignedAgentId;
    }

    public List<AgentKpi> ComputeAgentKpis(AppDatabase database)
    {
        var agents = database.Users.Where(user => user.Role == UserRole.Agent).ToList();

        return agents
            .Select(agent =>
            {
                var resolvedTickets = database.Tickets
                    .Where(ticket => ticket.Status == TicketStatus.Closed && GetCreditedAgentId(ticket) == agent.Id)
                    .ToList();

                var ratedTickets = resolvedTickets.Where(ticket => ticket.Feedback is not null).ToList();

                return new AgentKpi
                {
                    AgentId = agent.Id,
                    AgentName = agent.FullName,
                    TotalResolved = resolvedTickets.Count,
                    ActiveTickets = database.Tickets.Count(ticket =>
                        ticket.AssignedAgentId == agent.Id && ticket.Status != TicketStatus.Closed),
                    AverageRating = ratedTickets.Count == 0
                        ? null
                        : Math.Round(ratedTickets.Average(ticket => ticket.Feedback!.Rating), 2),
                    RatingCount = ratedTickets.Count,
                    AverageResolutionMinutes = resolvedTickets.Count == 0
                        ? null
                        : Math.Round(resolvedTickets.Average(ticket => (ticket.ClosedAtUtc!.Value - ticket.CreatedAtUtc).TotalMinutes), 1),
                    SlaComplianceRate = resolvedTickets.Count == 0
                        ? null
                        : Math.Round(100.0 * resolvedTickets.Count(ticket => ticket.ClosedAtUtc <= ticket.DueAtUtc) / resolvedTickets.Count, 1)
                };
            })
            .OrderBy(kpi => kpi.AgentName)
            .ToList();
    }

    public List<ResolvedTicketRow> GetResolvedTickets(AppDatabase database)
    {
        return database.Tickets
            .Where(ticket => ticket.Status == TicketStatus.Closed && ticket.ClosedAtUtc is not null)
            .OrderByDescending(ticket => ticket.ClosedAtUtc)
            .Select(ticket =>
            {
                var agentId = GetCreditedAgentId(ticket);
                var agent = database.Users.FirstOrDefault(user => user.Id == agentId);
                var requester = database.Users.FirstOrDefault(user => user.Id == ticket.RequesterId);

                return new ResolvedTicketRow
                {
                    Number = ticket.Number,
                    Title = ticket.Title,
                    Category = ticket.Category,
                    Priority = ticket.Priority.ToString(),
                    AgentName = agent?.FullName ?? "Няма назначен агент",
                    RequesterName = requester?.FullName ?? "Неизвестен потребител",
                    CreatedAtUtc = ticket.CreatedAtUtc,
                    ClosedAtUtc = ticket.ClosedAtUtc!.Value,
                    ResolutionMinutes = (int)Math.Max(0, (ticket.ClosedAtUtc.Value - ticket.CreatedAtUtc).TotalMinutes),
                    Rating = ticket.Feedback?.Rating,
                    MetSla = ticket.ClosedAtUtc <= ticket.DueAtUtc
                };
            })
            .ToList();
    }

    //генерира .xlsx файл с два листа - обобщение по агенти и пълен списък решени тикети
    public byte[] GenerateExcelReport(List<AgentKpi> agentKpis, List<ResolvedTicketRow> resolvedTickets)
    {
        using var workbook = new XLWorkbook();

        var summarySheet = workbook.Worksheets.Add("Обобщение по агенти");
        var summaryHeaders = new[]
        {
            "Агент", "Решени тикети", "Активни в момента", "Средна оценка",
            "Брой оценки", "Средно време до затваряне (мин.)", "Спазен SLA (%)"
        };
        for (var col = 0; col < summaryHeaders.Length; col++)
        {
            summarySheet.Cell(1, col + 1).Value = summaryHeaders[col];
        }

        summarySheet.Row(1).Style.Font.Bold = true;

        var summaryRow = 2;
        foreach (var kpi in agentKpis)
        {
            summarySheet.Cell(summaryRow, 1).Value = kpi.AgentName;
            summarySheet.Cell(summaryRow, 2).Value = kpi.TotalResolved;
            summarySheet.Cell(summaryRow, 3).Value = kpi.ActiveTickets;
            if (kpi.AverageRating.HasValue) summarySheet.Cell(summaryRow, 4).Value = kpi.AverageRating.Value;
            summarySheet.Cell(summaryRow, 5).Value = kpi.RatingCount;
            if (kpi.AverageResolutionMinutes.HasValue) summarySheet.Cell(summaryRow, 6).Value = kpi.AverageResolutionMinutes.Value;
            if (kpi.SlaComplianceRate.HasValue) summarySheet.Cell(summaryRow, 7).Value = kpi.SlaComplianceRate.Value;
            summaryRow++;
        }

        summarySheet.Columns().AdjustToContents();

        var detailSheet = workbook.Worksheets.Add("Решени тикети");
        var detailHeaders = new[]
        {
            "Номер", "Заглавие", "Категория", "Приоритет", "Агент", "Заявител",
            "Създаден", "Затворен", "Време до затваряне (мин.)", "Оценка", "Спазен SLA"
        };
        for (var col = 0; col < detailHeaders.Length; col++)
        {
            detailSheet.Cell(1, col + 1).Value = detailHeaders[col];
        }

        detailSheet.Row(1).Style.Font.Bold = true;

        var detailRow = 2;
        foreach (var ticket in resolvedTickets)
        {
            detailSheet.Cell(detailRow, 1).Value = ticket.Number;
            detailSheet.Cell(detailRow, 2).Value = ticket.Title;
            detailSheet.Cell(detailRow, 3).Value = ticket.Category;
            detailSheet.Cell(detailRow, 4).Value = ticket.Priority;
            detailSheet.Cell(detailRow, 5).Value = ticket.AgentName;
            detailSheet.Cell(detailRow, 6).Value = ticket.RequesterName;
            detailSheet.Cell(detailRow, 7).Value = ticket.CreatedAtUtc;
            detailSheet.Cell(detailRow, 8).Value = ticket.ClosedAtUtc;
            detailSheet.Cell(detailRow, 9).Value = ticket.ResolutionMinutes;
            if (ticket.Rating.HasValue) detailSheet.Cell(detailRow, 10).Value = ticket.Rating.Value;
            detailSheet.Cell(detailRow, 11).Value = ticket.MetSla ? "Да" : "Не";
            detailRow++;
        }

        detailSheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
