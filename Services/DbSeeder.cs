using Microsoft.EntityFrameworkCore;
using TicketSystem.Data;
using TicketSystem.Models;

namespace TicketSystem.Services;

//еднократно seed-ва базата при празна инсталация (аналог на старото EnsureDatabaseExists)
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext context)
    {
        if (await context.Users.AnyAsync())
        {
            return;
        }

        var now = DateTime.UtcNow;

        //демо парола "123" за всички seed потребители - хеширана отделно за всеки (различна сол на ред)
        static string Hash() => BCrypt.Net.BCrypt.HashPassword("123");

        var users = new List<AppUser>
        {
            new() { FullName = "admin", Email = "admin", Password = Hash(), Role = UserRole.Admin },
            new()
            {
                FullName = "agent1", Email = "agent1", Password = Hash(), Role = UserRole.Agent,
                Specializations = new List<string> { "Достъп и права", "Организационни промени" }
            },
            new()
            {
                FullName = "agent2", Email = "agent2", Password = Hash(), Role = UserRole.Agent,
                Specializations = new List<string> { "Кредитни операции", "Системни инциденти" }
            },
            new()
            {
                FullName = "agent3", Email = "agent3", Password = Hash(), Role = UserRole.Agent,
                Specializations = new List<string> { "Кредитни операции", "Достъп и права" }
            },
            new() { FullName = "user1", Email = "user1", Password = Hash(), Role = UserRole.User },
            new() { FullName = "user2", Email = "user2", Password = Hash(), Role = UserRole.User }
        };

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        var agent1 = users[1];
        var agent2 = users[2];
        var user1 = users[4];

        var tickets = new List<Ticket>
        {
            new()
            {
                Number = "TCK-1001",
                Title = "Достъп до кредитната система за нов служител",
                Description = "Новоназначен служител в офис София се нуждае от достъп до основната кредитна система.",
                Category = "Достъп и права",
                Priority = TicketPriority.High,
                Status = TicketStatus.Open,
                RequesterId = user1.Id,
                AssignedAgentId = agent1.Id,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now.AddMinutes(-35),
                DueAtUtc = TicketRepository.CalculateDueDate(now.AddHours(-1), TicketPriority.High)
            },
            new()
            {
                Number = "TCK-1002",
                Title = "Прехвърляне на кредит между офиси",
                Description = "Кредит с номер на договор 4521 трябва да бъде прехвърлен от клон София към клон Пловдив.",
                Category = "Кредитни операции",
                Priority = TicketPriority.Medium,
                Status = TicketStatus.Closed,
                RequesterId = user1.Id,
                AssignedAgentId = agent2.Id,
                CreatedAtUtc = now.AddHours(-10),
                UpdatedAtUtc = now.AddHours(-2),
                DueAtUtc = TicketRepository.CalculateDueDate(now.AddHours(-10), TicketPriority.Medium),
                StartedAtUtc = now.AddHours(-9),
                ClosedAtUtc = now.AddHours(-2)
            }
        };

        context.Tickets.AddRange(tickets);
        await context.SaveChangesAsync();
    }
}
