using TicketSystem.Models;
using TicketSystem.Services;
using Xunit;

namespace TicketSystem.Tests;

//тества алгоритъма за автоматично разпределение: тегло на натовареност -> специализация -> Id
public class TicketAssignmentServiceTests
{
    private readonly TicketAssignmentService _sut = new();

    private static AppUser Agent(int id, params string[] specializations) => new()
    {
        Id = id,
        FullName = $"agent{id}",
        Role = UserRole.Agent,
        Specializations = specializations.ToList()
    };

    private static Ticket ActiveTicket(int agentId, TicketPriority priority, TicketStatus status = TicketStatus.Open) => new()
    {
        AssignedAgentId = agentId,
        Priority = priority,
        Status = status
    };

    //период спрямо днес, за да не се обвързват тестовете с фиксирана дата
    private static Absence Absence(int agentId, AbsenceStatus status, int startOffsetDays = -1, int endOffsetDays = 1) => new()
    {
        AgentId = agentId,
        Status = status,
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(startOffsetDays),
        EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(endOffsetDays)
    };

    [Fact]
    public void SelectAgent_NoAgents_ReturnsNull()
    {
        var database = new AppDatabase { Users = new List<AppUser>() };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Null(result);
    }

    [Fact]
    public void SelectAgent_SingleAgent_ReturnsThatAgentRegardlessOfCategory()
    {
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1) }
        };

        var result = _sut.SelectAgent(database, "Кредитни операции");

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public void SelectAgent_PrefersAgentWithLowerWeightedLoad_EvenWithoutSpecializationMatch()
    {
        // agent1 е специализиран, но има един активен High тикет (тегло 12)
        // agent2 не е специализиран, но е напълно свободен (тегло 0)
        // натовареността е с приоритет пред специализацията - agent2 трябва да спечели
        var database = new AppDatabase
        {
            Users = new List<AppUser>
            {
                Agent(1, "Кредитни операции"),
                Agent(2)
            },
            Tickets = new List<Ticket>
            {
                ActiveTicket(1, TicketPriority.High)
            }
        };

        var result = _sut.SelectAgent(database, "Кредитни операции");

        Assert.Equal(2, result!.Id);
    }

    [Fact]
    public void SelectAgent_EqualLoad_TieBreaksBySpecializationMatch()
    {
        // и двамата имат по един Medium тикет (еднакво тегло) - само agent2 специализира в категорията
        var database = new AppDatabase
        {
            Users = new List<AppUser>
            {
                Agent(1),
                Agent(2, "Системни инциденти")
            },
            Tickets = new List<Ticket>
            {
                ActiveTicket(1, TicketPriority.Medium),
                ActiveTicket(2, TicketPriority.Medium)
            }
        };

        var result = _sut.SelectAgent(database, "Системни инциденти");

        Assert.Equal(2, result!.Id);
    }

    [Fact]
    public void SelectAgent_EqualLoadAndSpecialization_TieBreaksByLowestId()
    {
        // и двамата са напълно свободни и и двамата специализират - печели по-ниският Id
        var database = new AppDatabase
        {
            Users = new List<AppUser>
            {
                Agent(5, "Организационни промени"),
                Agent(2, "Организационни промени")
            }
        };

        var result = _sut.SelectAgent(database, "Организационни промени");

        Assert.Equal(2, result!.Id);
    }

    [Fact]
    public void SelectAgent_ClosedTicketsDoNotCountTowardLoad()
    {
        // agent1 има само затворени тикети (не би трябвало да тежат) -> все едно е свободен
        // agent2 има един активен High тикет -> по-натоварен
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1), Agent(2) },
            Tickets = new List<Ticket>
            {
                ActiveTicket(1, TicketPriority.High, TicketStatus.Closed),
                ActiveTicket(1, TicketPriority.High, TicketStatus.Closed),
                ActiveTicket(2, TicketPriority.High)
            }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Equal(1, result!.Id);
    }

    [Theory]
    [InlineData(TicketPriority.Low, TicketPriority.Medium)]
    [InlineData(TicketPriority.Medium, TicketPriority.High)]
    [InlineData(TicketPriority.Low, TicketPriority.High)]
    public void SelectAgent_LowerPriorityLoad_WeighsLessThanHigherPriorityLoad(TicketPriority lighter, TicketPriority heavier)
    {
        // agent1 носи по-лекия приоритет, agent2 - по-тежкия -> agent1 трябва да получи новия тикет
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1), Agent(2) },
            Tickets = new List<Ticket>
            {
                ActiveTicket(1, lighter),
                ActiveTicket(2, heavier)
            }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public void SelectAgent_AgentOnApprovedLeaveToday_IsExcluded()
    {
        // agent1 е свободен по натовареност, но е в одобрен отпуск днес -> печели agent2
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1), Agent(2) },
            Absences = new List<Absence> { Absence(1, AbsenceStatus.Approved) }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Equal(2, result!.Id);
    }

    [Theory]
    [InlineData(AbsenceStatus.Pending)]
    [InlineData(AbsenceStatus.Rejected)]
    [InlineData(AbsenceStatus.Cancelled)]
    public void SelectAgent_AgentOnNonApprovedLeave_IsNotExcluded(AbsenceStatus status)
    {
        // само Approved блокира разпределението - чакащи/отхвърлени/анулирани не пречат
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1) },
            Absences = new List<Absence> { Absence(1, status) }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public void SelectAgent_AllAgentsOnApprovedLeave_ReturnsNull()
    {
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1), Agent(2) },
            Absences = new List<Absence>
            {
                Absence(1, AbsenceStatus.Approved),
                Absence(2, AbsenceStatus.Approved)
            }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Null(result);
    }

    [Fact]
    public void SelectAgent_ApprovedLeaveOutsideDateRange_DoesNotExclude()
    {
        // отпуската е одобрена, но е приключила преди 5 дни - агентът вече е активен
        var database = new AppDatabase
        {
            Users = new List<AppUser> { Agent(1) },
            Absences = new List<Absence> { Absence(1, AbsenceStatus.Approved, startOffsetDays: -10, endOffsetDays: -5) }
        };

        var result = _sut.SelectAgent(database, "Достъп и права");

        Assert.Equal(1, result!.Id);
    }
}
