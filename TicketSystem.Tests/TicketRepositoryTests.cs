using TicketSystem.Models;
using TicketSystem.Services;
using Xunit;

namespace TicketSystem.Tests;

//тества чистата SLA логика (не изисква база данни - CalculateDueDate е static и няма странични ефекти)
public class TicketRepositoryTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CalculateDueDate_Low_AddsTwentyFourHours()
    {
        var dueAt = TicketRepository.CalculateDueDate(CreatedAt, TicketPriority.Low);

        Assert.Equal(CreatedAt.AddHours(24), dueAt);
    }

    [Fact]
    public void CalculateDueDate_Medium_AddsEightHours()
    {
        var dueAt = TicketRepository.CalculateDueDate(CreatedAt, TicketPriority.Medium);

        Assert.Equal(CreatedAt.AddHours(8), dueAt);
    }

    [Fact]
    public void CalculateDueDate_High_AddsTwoHours()
    {
        var dueAt = TicketRepository.CalculateDueDate(CreatedAt, TicketPriority.High);

        Assert.Equal(CreatedAt.AddHours(2), dueAt);
    }
}
