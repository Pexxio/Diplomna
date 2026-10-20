using TicketSystem.Models;

namespace TicketSystem.Services;

//алгоритъм за разпределение по натовареност (претеглена по спешност) и специализация
public sealed class TicketAssignmentService
{
    //избира агент: първо по най-ниска претеглена натовареност, после по специализация в категорията
    //агенти в одобрен отпуск днес не участват в разпределението
    public AppUser? SelectAgent(AppDatabase database, string category)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var agents = database.Users
            .Where(user => user.Role == UserRole.Agent)
            .Where(user => !IsOnApprovedLeave(database, user.Id, today))
            .ToList();

        if (agents.Count == 0)
        {
            return null;
        }

        return agents
            .OrderBy(agent => GetWeightedLoad(database, agent.Id))
            .ThenByDescending(agent => agent.Specializations.Contains(category) ? 1 : 0)
            .ThenBy(agent => agent.Id)
            .First();
    }

    //дали агентът има одобрено отсъствие, покриващо зададената дата
    private static bool IsOnApprovedLeave(AppDatabase database, int agentId, DateOnly date)
    {
        return database.Absences.Any(absence =>
            absence.AgentId == agentId &&
            absence.Status == AbsenceStatus.Approved &&
            absence.StartDate <= date &&
            date <= absence.EndDate);
    }

    //сумира теглото на всички активни (незатворени) тикети на агента
    private static int GetWeightedLoad(AppDatabase database, int agentId)
    {
        return database.Tickets
            .Where(ticket => ticket.AssignedAgentId == agentId && ticket.Status != TicketStatus.Closed)
            .Sum(ticket => GetPriorityWeight(ticket.Priority));
    }

    //тегло на приоритет = 24 / SLA часове (същите константи като в TicketRepository.CalculateDueDate)
    private static int GetPriorityWeight(TicketPriority priority)
    {
        return priority switch
        {
            TicketPriority.Low => 1,
            TicketPriority.Medium => 3,
            TicketPriority.High => 12,
            _ => 3
        };
    }
}
