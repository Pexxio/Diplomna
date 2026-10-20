using TicketSystem.Models;
using TicketSystem.Services;
using Xunit;

namespace TicketSystem.Tests;

//тества реалните KPI изчисления - без база данни, директно върху AppDatabase снимка
public class KpiServiceTests
{
    private readonly KpiService _sut = new();

    private static AppUser Agent(int id, string name) => new() { Id = id, FullName = name, Role = UserRole.Agent };

    private static AppUser Requester(int id, string name) => new() { Id = id, FullName = name, Role = UserRole.User };

    [Fact]
    public void ComputeAgentKpis_AgentWithNoResolvedTickets_ReturnsNullAveragesNotZero()
    {
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1, "agent1") }
        };

        var kpis = _sut.ComputeAgentKpis(database);

        var kpi = Assert.Single(kpis);
        Assert.Equal(0, kpi.TotalResolved);
        Assert.Null(kpi.AverageRating);
        Assert.Null(kpi.AverageResolutionMinutes);
        Assert.Null(kpi.SlaComplianceRate);
    }

    [Fact]
    public void ComputeAgentKpis_AveragesRatingOnlyAcrossRatedTickets()
    {
        var now = DateTime.UtcNow;
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1, "agent1") },
            Tickets = new List<Ticket>
            {
                ClosedTicket(id: 1, agentId: 1, createdAt: now.AddHours(-2), closedAt: now, dueAt: now.AddHours(1), rating: 5),
                ClosedTicket(id: 2, agentId: 1, createdAt: now.AddHours(-2), closedAt: now, dueAt: now.AddHours(1), rating: 3),
                //тикет без обратна връзка - не бива да влиза в средната оценка
                ClosedTicket(id: 3, agentId: 1, createdAt: now.AddHours(-2), closedAt: now, dueAt: now.AddHours(1), rating: null)
            }
        };

        var kpi = Assert.Single(_sut.ComputeAgentKpis(database));

        Assert.Equal(3, kpi.TotalResolved);
        Assert.Equal(2, kpi.RatingCount);
        Assert.Equal(4.0, kpi.AverageRating);
    }

    [Fact]
    public void ComputeAgentKpis_CreditsClosedByAgent_NotOriginallyAssignedAgent()
    {
        // тикет назначен на agent1, но реално затворен от agent2 (напр. admin е преразпределил преди затваряне)
        // -> заслугата трябва да отиде при agent2, не при agent1
        var now = DateTime.UtcNow;
        var ticket = ClosedTicket(id: 1, agentId: 1, createdAt: now.AddHours(-1), closedAt: now, dueAt: now.AddHours(1), rating: null);
        ticket.ClosedByAgentId = 2;

        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1, "agent1"), Agent(2, "agent2") },
            Tickets = new List<Ticket> { ticket }
        };

        var kpis = _sut.ComputeAgentKpis(database);

        Assert.Equal(0, kpis.Single(k => k.AgentId == 1).TotalResolved);
        Assert.Equal(1, kpis.Single(k => k.AgentId == 2).TotalResolved);
    }

    [Fact]
    public void ComputeAgentKpis_ActiveTickets_CountsOnlyNonClosedAssignedTickets()
    {
        var now = DateTime.UtcNow;
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1, "agent1") },
            Tickets = new List<Ticket>
            {
                new() { AssignedAgentId = 1, Status = TicketStatus.Open },
                new() { AssignedAgentId = 1, Status = TicketStatus.InProgress },
                ClosedTicket(id: 3, agentId: 1, createdAt: now.AddHours(-1), closedAt: now, dueAt: now.AddHours(1), rating: null)
            }
        };

        var kpi = Assert.Single(_sut.ComputeAgentKpis(database));

        Assert.Equal(2, kpi.ActiveTickets);
    }

    [Fact]
    public void ComputeAgentKpis_SlaComplianceRate_CountsOnlyTicketsClosedBeforeDueDate()
    {
        var now = DateTime.UtcNow;
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1, "agent1") },
            Tickets = new List<Ticket>
            {
                //затворен преди крайния срок - спазен SLA
                ClosedTicket(id: 1, agentId: 1, createdAt: now.AddHours(-3), closedAt: now, dueAt: now.AddHours(1), rating: null),
                //затворен след крайния срок - просрочен
                ClosedTicket(id: 2, agentId: 1, createdAt: now.AddHours(-3), closedAt: now, dueAt: now.AddHours(-1), rating: null)
            }
        };

        var kpi = Assert.Single(_sut.ComputeAgentKpis(database));

        Assert.Equal(50.0, kpi.SlaComplianceRate);
    }

    [Fact]
    public void GetResolvedTickets_ExcludesTicketsThatAreNotClosed()
    {
        var database = new AppDatabase
        {
            Tickets = new List<Ticket>
            {
                new() { Status = TicketStatus.Open },
                new() { Status = TicketStatus.InProgress },
                new() { Status = TicketStatus.Reopened }
            }
        };

        var rows = _sut.GetResolvedTickets(database);

        Assert.Empty(rows);
    }

    [Fact]
    public void GetResolvedTickets_OrdersByClosedDateDescending()
    {
        var now = DateTime.UtcNow;
        var database = new AppDatabase
        {
            Tickets = new List<Ticket>
            {
                ClosedTicket(id: 1, agentId: null, createdAt: now.AddDays(-2), closedAt: now.AddDays(-2), dueAt: now, rating: null, number: "TCK-1001"),
                ClosedTicket(id: 2, agentId: null, createdAt: now.AddDays(-1), closedAt: now, dueAt: now, rating: null, number: "TCK-1002")
            }
        };

        var rows = _sut.GetResolvedTickets(database);

        Assert.Equal("TCK-1002", rows[0].Number);
        Assert.Equal("TCK-1001", rows[1].Number);
    }

    [Fact]
    public void GetResolvedTickets_UnknownRequesterAndAgent_FallsBackToPlaceholderNames()
    {
        var now = DateTime.UtcNow;
        var ticket = ClosedTicket(id: 1, agentId: 99, createdAt: now.AddHours(-1), closedAt: now, dueAt: now.AddHours(1), rating: null);
        ticket.RequesterId = 99;

        var database = new AppDatabase
        {
            Users = new List<AppUser>(), //никой не съществува
            Tickets = new List<Ticket> { ticket }
        };

        var row = Assert.Single(_sut.GetResolvedTickets(database));

        Assert.Equal("Няма назначен агент", row.AgentName);
        Assert.Equal("Неизвестен потребител", row.RequesterName);
    }

    private static Ticket ClosedTicket(
        int id,
        int? agentId,
        DateTime createdAt,
        DateTime closedAt,
        DateTime dueAt,
        int? rating,
        string number = "TCK-0000")
    {
        return new Ticket
        {
            Id = id,
            Number = number,
            Status = TicketStatus.Closed,
            AssignedAgentId = agentId,
            CreatedAtUtc = createdAt,
            ClosedAtUtc = closedAt,
            DueAtUtc = dueAt,
            Feedback = rating is null ? null : new TicketFeedback { Rating = rating.Value }
        };
    }
}
