namespace TicketSystem.Models;

//резултат от опит за изтриване на агент
public enum DeleteAgentResult
{
    Success,
    NotFound,
    HasActiveTickets
}
