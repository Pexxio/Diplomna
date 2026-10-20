using Microsoft.EntityFrameworkCore;
using TicketSystem.Data;
using TicketSystem.Models;

namespace TicketSystem.Services;

//капсулира достъпа до данните през EF Core / PostgreSQL (замества старото файлово JSON съхранение)
public sealed class TicketRepository
{
    private readonly AppDbContext _context;
    private readonly TicketAssignmentService _assignmentService;

    public TicketRepository(AppDbContext context, TicketAssignmentService assignmentService)
    {
        _context = context;
        _assignmentService = assignmentService;
    }

    //извлича пълна снимка на данните - използва се от TicketAssignmentService/KpiService и за DTO мапинг
    public async Task<AppDatabase> LoadAsync()
    {
        var tickets = await _context.Tickets.Include(ticket => ticket.Comments).AsNoTracking().ToListAsync();

        //само метаданни за прикачените файлове - Content не се select-ва тук, за да не се тегли
        //потенциално по няколко MB на тикет само за показване на списъка
        var attachmentMetadata = await _context.Attachments
            .Select(attachment => new TicketAttachment
            {
                Id = attachment.Id,
                TicketId = attachment.TicketId,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                SizeBytes = attachment.SizeBytes,
                UploadedByUserId = attachment.UploadedByUserId,
                UploadedAtUtc = attachment.UploadedAtUtc
            })
            .AsNoTracking()
            .ToListAsync();

        var attachmentsByTicket = attachmentMetadata.ToLookup(attachment => attachment.TicketId);
        foreach (var ticket in tickets)
        {
            ticket.Attachments = attachmentsByTicket[ticket.Id].ToList();
        }

        return new AppDatabase
        {
            Users = await _context.Users.AsNoTracking().ToListAsync(),
            Tickets = tickets,
            Notifications = await _context.Notifications.AsNoTracking().ToListAsync(),
            Absences = await _context.Absences.AsNoTracking().ToListAsync()
        };
    }

    //последните (различни) двойки категория/подкатегория от собствените тикети на потребителя, най-новите първи
    public async Task<List<RecentCategory>> GetRecentCategoriesForUserAsync(int userId, int count = 3)
    {
        var recentCategories = await _context.Tickets
            .Where(ticket => ticket.RequesterId == userId)
            .OrderByDescending(ticket => ticket.CreatedAtUtc)
            .Select(ticket => new RecentCategory { TopCategory = ticket.Category, SubCategory = ticket.SubCategory })
            .Take(50)
            .ToListAsync();

        var distinct = new List<RecentCategory>();
        var seen = new HashSet<(string, string)>();
        foreach (var item in recentCategories)
        {
            if (seen.Add((item.TopCategory, item.SubCategory)))
            {
                distinct.Add(item);
                if (distinct.Count >= count)
                {
                    break;
                }
            }
        }

        return distinct;
    }

    //създава нов агент с зададено от админ потребителско име и първична парола
    public async Task<AppUser?> CreateAgentAsync(string username, string password)
    {
        var exists = await _context.Users.AnyAsync(user => user.Email.ToLower() == username.ToLower());
        if (exists)
        {
            return null;
        }

        var agent = new AppUser
        {
            FullName = username,
            Email = username,
            Password = BCrypt.Net.BCrypt.HashPassword(password),
            Role = UserRole.Agent
        };

        _context.Users.Add(agent);
        await _context.SaveChangesAsync();
        return agent;
    }

    //изтрива агент - отказва, ако в момента държи активни (незатворени) тикети
    public async Task<DeleteAgentResult> DeleteAgentAsync(int agentId)
    {
        var agent = await _context.Users.FirstOrDefaultAsync(user => user.Id == agentId && user.Role == UserRole.Agent);
        if (agent is null)
        {
            return DeleteAgentResult.NotFound;
        }

        var hasActiveTickets = await _context.Tickets.AnyAsync(ticket =>
            ticket.AssignedAgentId == agentId && ticket.Status != TicketStatus.Closed);
        if (hasActiveTickets)
        {
            return DeleteAgentResult.HasActiveTickets;
        }

        _context.Users.Remove(agent);
        await _context.SaveChangesAsync();
        return DeleteAgentResult.Success;
    }

    //редактира специализациите на съществуващ агент
    public async Task<AppUser?> UpdateAgentSpecializationsAsync(int agentId, List<string> specializations)
    {
        var agent = await _context.Users.FirstOrDefaultAsync(user => user.Id == agentId && user.Role == UserRole.Agent);
        if (agent is null)
        {
            return null;
        }

        agent.Specializations = specializations;
        await _context.SaveChangesAsync();
        return agent;
    }

    //заявка за отпуска, подадена от самия агент - изчаква решение на админ
    public async Task<Absence> RequestAbsenceAsync(int agentId, DateOnly startDate, DateOnly endDate, AbsenceType type, string reason)
    {
        var absence = new Absence
        {
            AgentId = agentId,
            StartDate = startDate,
            EndDate = endDate,
            Type = type,
            Reason = reason.Trim(),
            Status = AbsenceStatus.Pending,
            RequestedByUserId = agentId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.Absences.Add(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    //отсъствие, въведено директно от админ за агент (напр. болничен) - вече одобрено, без чакане
    public async Task<Absence?> CreateApprovedAbsenceAsync(
        int agentId, DateOnly startDate, DateOnly endDate, AbsenceType type, string reason, int adminUserId)
    {
        var agent = await _context.Users.FirstOrDefaultAsync(user => user.Id == agentId && user.Role == UserRole.Agent);
        if (agent is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var absence = new Absence
        {
            AgentId = agentId,
            StartDate = startDate,
            EndDate = endDate,
            Type = type,
            Reason = reason.Trim(),
            Status = AbsenceStatus.Approved,
            RequestedByUserId = adminUserId,
            DecidedByUserId = adminUserId,
            CreatedAtUtc = now,
            DecidedAtUtc = now
        };

        _context.Absences.Add(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    //одобрява/отхвърля чакаща заявка за отпуска
    public async Task<Absence?> DecideAbsenceAsync(int absenceId, bool approve, int adminUserId)
    {
        var absence = await _context.Absences.FirstOrDefaultAsync(item => item.Id == absenceId);
        if (absence is null || absence.Status != AbsenceStatus.Pending)
        {
            return null;
        }

        absence.Status = approve ? AbsenceStatus.Approved : AbsenceStatus.Rejected;
        absence.DecidedByUserId = adminUserId;
        absence.DecidedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return absence;
    }

    //анулира чакаща или вече одобрена отпуска (демонстрационна цел - връща агента веднага в разпределението)
    //извиква се и от Admin (чуждо отсъствие), и от самия Agent (собственото му) - собствеността се проверява на ниво Program.cs
    public async Task<Absence?> CancelAbsenceAsync(int absenceId, int cancelledByUserId)
    {
        var absence = await _context.Absences.FirstOrDefaultAsync(item => item.Id == absenceId);
        if (absence is null || absence.Status is AbsenceStatus.Rejected or AbsenceStatus.Cancelled)
        {
            return null;
        }

        absence.Status = AbsenceStatus.Cancelled;
        absence.DecidedByUserId = cancelledByUserId;
        absence.DecidedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return absence;
    }

    //качва прикачен файл към тикет - валидира размер/тип/брой и че тикетът все още не е затворен
    public async Task<(AddAttachmentResult Result, TicketAttachment? Attachment)> AddAttachmentAsync(
        int ticketId, string fileName, string contentType, byte[] content, int uploadedByUserId)
    {
        var ticket = await _context.Tickets.FirstOrDefaultAsync(item => item.Id == ticketId);
        if (ticket is null)
        {
            return (AddAttachmentResult.TicketNotFound, null);
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            return (AddAttachmentResult.TicketClosed, null);
        }

        if (!AttachmentValidator.IsAllowedFileName(fileName))
        {
            return (AddAttachmentResult.DisallowedFileType, null);
        }

        if (!AttachmentValidator.IsWithinSizeLimit(content.LongLength))
        {
            return (AddAttachmentResult.FileTooLarge, null);
        }

        var currentCount = await _context.Attachments.CountAsync(item => item.TicketId == ticketId);
        if (currentCount >= AttachmentValidator.MaxAttachmentsPerTicket)
        {
            return (AddAttachmentResult.TooManyAttachments, null);
        }

        var attachment = new TicketAttachment
        {
            TicketId = ticketId,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = content.LongLength,
            Content = content,
            UploadedByUserId = uploadedByUserId,
            UploadedAtUtc = DateTime.UtcNow
        };

        _context.Attachments.Add(attachment);
        await _context.SaveChangesAsync();
        return (AddAttachmentResult.Success, attachment);
    }

    //пълния запис, вкл. Content - само за сваляне
    public async Task<TicketAttachment?> GetAttachmentAsync(int ticketId, int attachmentId)
    {
        return await _context.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == attachmentId && item.TicketId == ticketId);
    }

    //трие прикачен файл - позволено само на качилия го или на Admin
    public async Task<bool> DeleteAttachmentAsync(int ticketId, int attachmentId, AppUser currentUser)
    {
        var attachment = await _context.Attachments
            .FirstOrDefaultAsync(item => item.Id == attachmentId && item.TicketId == ticketId);

        if (attachment is null)
        {
            return false;
        }

        if (attachment.UploadedByUserId != currentUser.Id && currentUser.Role != UserRole.Admin)
        {
            return false;
        }

        _context.Attachments.Remove(attachment);
        await _context.SaveChangesAsync();
        return true;
    }

    //създава тикет и го разпределя на агент по алгоритъма
    public async Task<Ticket> CreateTicketAsync(int requesterId, string title, string description, string category, string subCategory, TicketPriority priority)
    {
        var database = await LoadAsync();
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Общо" : category.Trim();
        var normalizedSubCategory = subCategory?.Trim() ?? string.Empty;
        var assignedAgent = _assignmentService.SelectAgent(database, normalizedCategory);
        var now = DateTime.UtcNow;

        var ticket = new Ticket
        {
            Title = title.Trim(),
            Description = description.Trim(),
            Category = normalizedCategory,
            SubCategory = normalizedSubCategory,
            Priority = priority,
            Status = TicketStatus.Open,
            RequesterId = requesterId,
            AssignedAgentId = assignedAgent?.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            DueAtUtc = CalculateDueDate(now, priority)
        };

        _context.Tickets.Add(ticket);
        await _context.SaveChangesAsync();

        //номерът зависи от генерирания от базата Id, затова се задава след първия запис
        ticket.Number = $"TCK-{1000 + ticket.Id}";

        if (assignedAgent is not null)
        {
            AddAgentAssignmentNotification(assignedAgent.Id, ticket);
        }

        await _context.SaveChangesAsync();
        return ticket;
    }

    //функция за преразпределяне
    public async Task<Ticket?> ReassignTicketAsync(int ticketId, int agentId, AppUser currentUser)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);
        var agentExists = await _context.Users.AnyAsync(user => user.Id == agentId && user.Role == UserRole.Agent);

        if (ticket is null || !agentExists)
        {
            return null;
        }

        ticket.AssignedAgentId = agentId;
        ticket.UpdatedAtUtc = DateTime.UtcNow;
        AddRequesterNotification(ticket, currentUser.Id, $"Тикет {ticket.Number} е преразпределен към друг агент.");
        AddAgentAssignmentNotification(agentId, ticket);

        await _context.SaveChangesAsync();
        return ticket;
    }

    //функция за триене
    public async Task<bool> DeleteTicketAsync(int ticketId)
    {
        var ticket = await _context.Tickets.FirstOrDefaultAsync(item => item.Id == ticketId);
        if (ticket is null)
        {
            return false;
        }

        AddNotification(ticket.RequesterId, ticket.Id, $"Тикет {ticket.Number} е изтрит от администратор.", "DeletedTicket");
        if (ticket.AssignedAgentId is not null && ticket.AssignedAgentId.Value != ticket.RequesterId)
        {
            AddNotification(ticket.AssignedAgentId.Value, ticket.Id, $"Тикет {ticket.Number} е изтрит от администратор.", "DeletedTicket");
        }

        _context.Tickets.Remove(ticket);
        await _context.SaveChangesAsync();
        return true;
    }

    //функция за започване на тикет
    public async Task<Ticket?> StartTicketAsync(int ticketId, AppUser currentUser)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);

        if (ticket is null || ticket.Status == TicketStatus.Closed)
        {
            return null;
        }

        var canStart =
            currentUser.Role == UserRole.Admin ||
            (currentUser.Role == UserRole.Agent && ticket.AssignedAgentId == currentUser.Id);

        if (!canStart)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        ticket.Status = TicketStatus.InProgress;
        ticket.StartedAtUtc ??= now;
        ticket.UpdatedAtUtc = now;
        AddRequesterNotification(ticket, currentUser.Id, $"Тикет {ticket.Number} е маркиран като в прогрес.");

        await _context.SaveChangesAsync();
        return ticket;
    }

    //функция за затваряне на тикет
    public async Task<Ticket?> CloseTicketAsync(int ticketId, AppUser currentUser)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);

        if (ticket is null)
        {
            return null;
        }

        var canClose =
            currentUser.Role == UserRole.Admin ||
            (currentUser.Role == UserRole.Agent && ticket.AssignedAgentId == currentUser.Id);

        if (!canClose || ticket.Status != TicketStatus.InProgress)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        ticket.Status = TicketStatus.Closed;
        ticket.StartedAtUtc ??= now;
        ticket.ClosedAtUtc = now;
        ticket.ClosedByAgentId = currentUser.Role == UserRole.Agent ? currentUser.Id : ticket.AssignedAgentId;
        ticket.UpdatedAtUtc = now;
        AddRequesterNotification(
            ticket,
            currentUser.Id,
            $"Тикет {ticket.Number} е затворен. Моля, оставете обратна връзка.",
            "FeedbackRequest");

        await _context.SaveChangesAsync();
        return ticket;
    }

    //функция за повторно отваряне
    public async Task<Ticket?> ReopenTicketAsync(int ticketId, AppUser currentUser)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);

        if (ticket is null || ticket.RequesterId != currentUser.Id || ticket.Status != TicketStatus.Closed)
        {
            return null;
        }

        ticket.Status = TicketStatus.Reopened;
        ticket.StartedAtUtc = null;
        ticket.ClosedAtUtc = null;
        ticket.ClosedByAgentId = null;
        ticket.Feedback = null;
        ticket.UpdatedAtUtc = DateTime.UtcNow;
        if (ticket.AssignedAgentId is not null)
        {
            AddNotification(ticket.AssignedAgentId.Value, ticket.Id, $"Тикет {ticket.Number} е отворен отново.");
        }

        await _context.SaveChangesAsync();
        return ticket;
    }

    //функция за добавяне на коментар
    public async Task<Ticket?> AddCommentAsync(int ticketId, AppUser currentUser, string text)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);

        if (ticket is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ticket.Comments.Add(new TicketComment
        {
            AuthorId = currentUser.Id,
            Text = text.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
        ticket.UpdatedAtUtc = DateTime.UtcNow;
        AddCommentNotifications(ticket, currentUser);

        await _context.SaveChangesAsync();
        return ticket;
    }

    public async Task<Ticket?> SubmitFeedbackAsync(int ticketId, AppUser currentUser, int rating, string comment)
    {
        var ticket = await _context.Tickets
            .Include(item => item.Comments)
            .Include(item => item.Attachments)
            .FirstOrDefaultAsync(item => item.Id == ticketId);

        if (ticket is null ||
            ticket.RequesterId != currentUser.Id ||
            ticket.Status != TicketStatus.Closed ||
            ticket.Feedback is not null ||
            rating is < 1 or > 5)
        {
            return null;
        }

        ticket.Feedback = new TicketFeedback
        {
            UserId = currentUser.Id,
            Rating = rating,
            Comment = string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        ticket.UpdatedAtUtc = DateTime.UtcNow;

        var agentId = ticket.ClosedByAgentId ?? ticket.AssignedAgentId;
        if (agentId is not null && agentId.Value != currentUser.Id)
        {
            AddNotification(agentId.Value, ticket.Id, $"Получена е оценка {rating}/5 за тикет {ticket.Number}.", "OpenTicket");
        }

        await _context.SaveChangesAsync();
        return ticket;
    }

    public async Task<List<TicketNotification>> GetNotificationsForUserAsync(int userId)
    {
        return await _context.Notifications
            .Where(notification => notification.UserId == userId)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Take(30)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<bool> MarkNotificationReadAsync(int notificationId, int userId)
    {
        var notification = await _context.Notifications
            .FirstOrDefaultAsync(item => item.Id == notificationId && item.UserId == userId);
        if (notification is null)
        {
            return false;
        }

        notification.IsRead = true;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<int> MarkAllNotificationsReadAsync(int userId)
    {
        var notifications = await _context.Notifications
            .Where(notification => notification.UserId == userId && !notification.IsRead)
            .ToListAsync();

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
        }

        if (notifications.Count > 0)
        {
            await _context.SaveChangesAsync();
        }

        return notifications.Count;
    }

    //изчисление на остатъчен срок
    public static DateTime CalculateDueDate(DateTime createdAtUtc, TicketPriority priority)
    {
        return priority switch
        {
            TicketPriority.Low => createdAtUtc.AddHours(24),
            TicketPriority.Medium => createdAtUtc.AddHours(8),
            TicketPriority.High => createdAtUtc.AddHours(2),
            _ => createdAtUtc.AddHours(8)
        };
    }

    private void AddRequesterNotification(
        Ticket ticket,
        int actorId,
        string message,
        string type = "OpenTicket")
    {
        if (ticket.RequesterId == actorId)
        {
            return;
        }

        AddNotification(ticket.RequesterId, ticket.Id, message, type);
    }

    private void AddAgentAssignmentNotification(int agentId, Ticket ticket)
    {
        AddNotification(agentId, ticket.Id, $"Тикет {ticket.Number} е назначен на вас.", "OpenTicket");
    }

    private void AddCommentNotifications(Ticket ticket, AppUser author)
    {
        var recipients = new HashSet<int> { ticket.RequesterId };
        if (ticket.AssignedAgentId is not null)
        {
            recipients.Add(ticket.AssignedAgentId.Value);
        }

        recipients.Remove(author.Id);

        foreach (var recipientId in recipients)
        {
            AddNotification(recipientId, ticket.Id, $"Има нов коментар по тикет {ticket.Number}.", "OpenTicket");
        }
    }

    private void AddNotification(int userId, int ticketId, string message, string type = "OpenTicket")
    {
        _context.Notifications.Add(new TicketNotification
        {
            UserId = userId,
            TicketId = ticketId,
            Message = message,
            Type = type,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
