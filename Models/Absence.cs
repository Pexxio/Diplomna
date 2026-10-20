namespace TicketSystem.Models;

//отсъствие (отпуска) на агент - болничен/платен/неплатен, за период от дни
public sealed class Absence
{
    public int Id { get; set; }

    public int AgentId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public AbsenceType Type { get; set; }

    public string Reason { get; set; } = string.Empty;

    public AbsenceStatus Status { get; set; } = AbsenceStatus.Pending;

    //кой го е завел - самият агент (заявка) или админ (директно въведено, напр. болничен)
    public int RequestedByUserId { get; set; }

    //кой админ е одобрил/отхвърлил/анулирал
    public int? DecidedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedAtUtc { get; set; }
}
