using TicketSystem.Models;

namespace TicketSystem.Services;

//in-memory снимка на данните, използвана от repository слоя и unit тестовете
public sealed class AppDatabase
{
    public List<AppUser> Users { get; set; } = new();

    public List<Ticket> Tickets { get; set; } = new();

    public List<TicketNotification> Notifications { get; set; } = new();

    public List<Absence> Absences { get; set; } = new();
}
