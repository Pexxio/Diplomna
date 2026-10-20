using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TicketSystem.Data;
using TicketSystem.Models;
using TicketSystem.Services;

var builder = WebApplication.CreateBuilder(args);

//добавя сървиси към depencency injection контейнера, за да може asp.net core сам да си подава
//класа към ендпойта
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<TicketAssignmentService>();
builder.Services.AddScoped<TicketRepository>();
builder.Services.AddSingleton<KpiService>();
builder.Services.AddSingleton<CategorySuggestionService>();

//пази кой потребител е вътре в системата
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".TicketSystem.Session";
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    //кара enum да се връща като стринг
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

//ограничава опитите за вход - до 5 на минута на IP адрес, срещу brute-force
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Твърде много опити за вход. Опитайте отново след малко." },
            token);
    };

    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();
app.UseRateLimiter();

//прилага миграциите и seed-ва базата при първо стартиране
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.MigrateAsync();
    await DbSeeder.SeedAsync(context);
}

app.MapPost("/api/login", async (LoginRequest request, TicketRepository repository, HttpContext context) =>
{
    var database = await repository.LoadAsync();

    var user = database.Users.FirstOrDefault(item =>
        item.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase));

    if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
    {
        return Results.Unauthorized();
    }

    context.Session.SetInt32("UserId", user.Id);
    return Results.Ok(ToUserDto(user));
}).RequireRateLimiting("login");

app.MapPost("/api/logout", (HttpContext context) =>
{
    context.Session.Clear();
    return Results.Ok(new { message = "Изходът е успешен." });
});

app.MapGet("/api/me", async (TicketRepository repository, HttpContext context) =>
{
    var user = await GetCurrentUserAsync(repository, context);
    return user is null ? Results.Unauthorized() : Results.Ok(ToUserDto(user));
});

app.MapGet("/api/users", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();

    return Results.Ok(database.Users.Select(ToUserDto));
});

app.MapPost("/api/agents", async (CreateAgentRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Потребителско име и парола са задължителни." });
    }

    var agent = await repository.CreateAgentAsync(request.Username.Trim(), request.Password);
    if (agent is null)
    {
        return Results.BadRequest(new { message = "Потребител с това име вече съществува." });
    }

    return Results.Created($"/api/users/{agent.Id}", ToUserDto(agent));
});

app.MapDelete("/api/agents/{id:int}", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var result = await repository.DeleteAgentAsync(id);
    return result switch
    {
        DeleteAgentResult.Success => Results.Ok(new { message = "Агентът е изтрит." }),
        DeleteAgentResult.HasActiveTickets => Results.BadRequest(new { message = "Агентът има активни тикети - преразпредели ги първо." }),
        _ => Results.NotFound(new { message = "Агентът не е намерен." })
    };
});

app.MapPut("/api/agents/{id:int}/specializations", async (int id, UpdateSpecializationsRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var agent = await repository.UpdateAgentSpecializationsAsync(id, request.Specializations ?? new List<string>());
    if (agent is null)
    {
        return Results.NotFound(new { message = "Агентът не е намерен." });
    }

    return Results.Ok(ToUserDto(agent));
});

//Admin вижда пълната история на всички отсъствия (за одобрение/отхвърляне); Agent вижда чакащите
//и одобрените на целия екип (не само своите) - отхвърлените/анулираните не са му от полза
app.MapGet("/api/absences", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin && currentUser.Role != UserRole.Agent)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var database = await repository.LoadAsync();
    var absences = currentUser.Role == UserRole.Admin
        ? database.Absences
        : database.Absences.Where(absence => absence.Status == AbsenceStatus.Pending || absence.Status == AbsenceStatus.Approved);

    return Results.Ok(absences
        .OrderByDescending(absence => absence.CreatedAtUtc)
        .Select(absence => ToAbsenceDto(absence, database)));
});

//пълната собствена история на агента (вкл. отхвърлени/анулирани/минали) - за "Моите отпуски"
app.MapGet("/api/absences/mine", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Agent)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var database = await repository.LoadAsync();
    var absences = database.Absences.Where(absence => absence.AgentId == currentUser.Id);

    return Results.Ok(absences
        .OrderByDescending(absence => absence.StartDate)
        .Select(absence => ToAbsenceDto(absence, database)));
});

//агент заявява собствена отпуска - изчаква одобрение
app.MapPost("/api/absences", async (CreateAbsenceRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Agent)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (request.EndDate < request.StartDate)
    {
        return Results.BadRequest(new { message = "Крайната дата трябва да е след началната." });
    }

    var absence = await repository.RequestAbsenceAsync(currentUser.Id, request.StartDate, request.EndDate, request.Type, request.Reason ?? string.Empty);
    var database = await repository.LoadAsync();
    return Results.Created($"/api/absences/{absence.Id}", ToAbsenceDto(absence, database));
});

//админ въвежда вече одобрена отпуска директно за избран агент (напр. болничен)
app.MapPost("/api/absences/admin", async (CreateAbsenceForAgentRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (request.EndDate < request.StartDate)
    {
        return Results.BadRequest(new { message = "Крайната дата трябва да е след началната." });
    }

    var absence = await repository.CreateApprovedAbsenceAsync(
        request.AgentId, request.StartDate, request.EndDate, request.Type, request.Reason ?? string.Empty, currentUser.Id);
    if (absence is null)
    {
        return Results.NotFound(new { message = "Агентът не е намерен." });
    }

    var database = await repository.LoadAsync();
    return Results.Created($"/api/absences/{absence.Id}", ToAbsenceDto(absence, database));
});

app.MapPost("/api/absences/{id:int}/decide", async (int id, DecideAbsenceRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var absence = await repository.DecideAbsenceAsync(id, request.Approve, currentUser.Id);
    if (absence is null)
    {
        return Results.BadRequest(new { message = "Отсъствието не е намерено или вече е решено." });
    }

    var database = await repository.LoadAsync();
    return Results.Ok(ToAbsenceDto(absence, database));
});

//анулира отсъствие (демонстрационна цел) - връща агента веднага в разпределението на тикети
//Admin може да анулира всяко отсъствие; Agent може да анулира само своето собствено (чакащо или одобрено)
app.MapPost("/api/absences/{id:int}/cancel", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin && currentUser.Role != UserRole.Agent)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var database = await repository.LoadAsync();
    var target = database.Absences.FirstOrDefault(absence => absence.Id == id);
    if (target is null)
    {
        return Results.BadRequest(new { message = "Отсъствието не може да бъде анулирано." });
    }

    if (currentUser.Role == UserRole.Agent && target.AgentId != currentUser.Id)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var absence = await repository.CancelAbsenceAsync(id, currentUser.Id);
    if (absence is null)
    {
        return Results.BadRequest(new { message = "Отсъствието не може да бъде анулирано." });
    }

    var updatedDatabase = await repository.LoadAsync();
    return Results.Ok(ToAbsenceDto(absence, updatedDatabase));
});

app.MapGet("/api/tickets", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();

    //определя кой вижда кои тикети
    var visibleTickets = database.Tickets
        .Where(ticket => CanViewTicket(currentUser, ticket))
        .ToList();
    var tickets = visibleTickets
        .Select(ticket => ToTicketDto(ticket, database))
        .ToList();
    var ratedTickets = currentUser.Role == UserRole.Agent
        ? visibleTickets
            .Where(ticket =>
                ticket.Status == TicketStatus.Closed &&
                ticket.Feedback is not null &&
                ticket.ClosedByAgentId == currentUser.Id)
            .ToList()
        : new List<Ticket>();
    return Results.Ok(new
    {
        tickets,
        stats = new
        {
            total = visibleTickets.Count,
            open = visibleTickets.Count(ticket => ticket.Status == TicketStatus.Open),
            inProgress = visibleTickets.Count(ticket => ticket.Status == TicketStatus.InProgress),
            reopened = visibleTickets.Count(ticket => ticket.Status == TicketStatus.Reopened),
            closed = visibleTickets.Count(ticket => ticket.Status == TicketStatus.Closed),
            overdue = visibleTickets.Count(ticket => ticket.Status != TicketStatus.Closed && ticket.DueAtUtc < DateTime.UtcNow),
            averageRating = currentUser.Role == UserRole.Agent && ratedTickets.Count > 0
                ? Math.Round(ratedTickets.Average(ticket => ticket.Feedback!.Rating), 2)
                : (double?)null,
            ratingCount = currentUser.Role == UserRole.Agent ? ratedTickets.Count : 0
        }
    });
});

app.MapGet("/api/tickets/categories", async (TicketRepository repository, CategorySuggestionService suggestionService, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(suggestionService.GetTaxonomy());
});

app.MapGet("/api/tickets/suggest-category", async (string? title, TicketRepository repository, CategorySuggestionService suggestionService, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(suggestionService.Suggest(title ?? string.Empty));
});

app.MapGet("/api/tickets/recent-categories", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var categories = await repository.GetRecentCategoriesForUserAsync(currentUser.Id);
    return Results.Ok(categories);
});

app.MapPost("/api/tickets", async (CreateTicketRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Description))
    {
        return Results.BadRequest(new { message = "Заглавие и описание са задължителни." });
    }

    if (currentUser.Role == UserRole.Agent)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var ticket = await repository.CreateTicketAsync(
        currentUser.Id,
        request.Title,
        request.Description,
        request.Category,
        request.SubCategory,
        request.Priority);

    var database = await repository.LoadAsync();
    return Results.Created($"/api/tickets/{ticket.Id}", ToTicketDto(ticket, database));
});

app.MapPost("/api/tickets/{id:int}/reassign", async (int id, ReassignTicketRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    //само админ може да преразпределя
    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var ticket = await repository.ReassignTicketAsync(id, request.AgentId, currentUser);
    if (ticket is null)
    {
        return Results.NotFound(new { message = "Тикетът или агентът не е намерен." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapDelete("/api/tickets/{id:int}", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return await repository.DeleteTicketAsync(id)
        ? Results.Ok(new { message = "Тикетът е изтрит." })
        : Results.NotFound(new { message = "Тикетът не е намерен." });
});

app.MapPost("/api/tickets/{id:int}/delete", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return await repository.DeleteTicketAsync(id)
        ? Results.Ok(new { message = "Тикетът е изтрит." })
        : Results.NotFound(new { message = "Тикетът не е намерен." });
});

app.MapPost("/api/tickets/{id:int}/start", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var ticket = await repository.StartTicketAsync(id, currentUser);
    if (ticket is null)
    {
        return Results.BadRequest(new { message = "Само назначен агент или администратор може да започне активен тикет." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapPost("/api/tickets/{id:int}/close", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var ticket = await repository.CloseTicketAsync(id, currentUser);
    if (ticket is null)
    {
        return Results.BadRequest(new { message = "Тикетът трябва първо да бъде маркиран като в прогрес." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapPost("/api/tickets/{id:int}/reopen", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    //само собствен тикет може да се отваря повторно
    var ticket = await repository.ReopenTicketAsync(id, currentUser);
    if (ticket is null)
    {
        return Results.BadRequest(new { message = "Само собствен затворен тикет може да бъде отворен отново." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapPost("/api/tickets/{id:int}/comments", async (int id, AddCommentRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();
    var existingTicket = database.Tickets.FirstOrDefault(ticket => ticket.Id == id);
    if (existingTicket is null || !CanViewTicket(currentUser, existingTicket))
    {
        return Results.NotFound();
    }

    var ticket = await repository.AddCommentAsync(id, currentUser, request.Text);
    if (ticket is null)
    {
        return Results.BadRequest(new { message = "Коментарът не може да бъде празен." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapPost("/api/tickets/{id:int}/attachments", async (int id, IFormFile file, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();
    var existingTicket = database.Tickets.FirstOrDefault(ticket => ticket.Id == id);
    if (existingTicket is null || !CanViewTicket(currentUser, existingTicket))
    {
        return Results.NotFound();
    }

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "Няма избран файл." });
    }

    await using var stream = new MemoryStream();
    await file.CopyToAsync(stream);
    var content = stream.ToArray();

    var (result, attachment) = await repository.AddAttachmentAsync(id, file.FileName, file.ContentType, content, currentUser.Id);

    return result switch
    {
        AddAttachmentResult.Success => Results.Ok(ToAttachmentDto(attachment!, currentUser.FullName)),
        AddAttachmentResult.TicketClosed => Results.BadRequest(new { message = "Не може да прикачаш файлове към затворен тикет." }),
        AddAttachmentResult.FileTooLarge => Results.BadRequest(new { message = $"Файлът е твърде голям - максимум {AttachmentValidator.MaxFileSizeBytes / (1024 * 1024)}MB." }),
        AddAttachmentResult.DisallowedFileType => Results.BadRequest(new { message = "Неразрешен тип файл." }),
        AddAttachmentResult.TooManyAttachments => Results.BadRequest(new { message = $"Максимум {AttachmentValidator.MaxAttachmentsPerTicket} прикачени файла на тикет." }),
        _ => Results.NotFound(new { message = "Тикетът не е намерен." })
    };
}).DisableAntiforgery();

app.MapGet("/api/tickets/{id:int}/attachments/{attachmentId:int}", async (int id, int attachmentId, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();
    var existingTicket = database.Tickets.FirstOrDefault(ticket => ticket.Id == id);
    if (existingTicket is null || !CanViewTicket(currentUser, existingTicket))
    {
        return Results.NotFound();
    }

    var attachment = await repository.GetAttachmentAsync(id, attachmentId);
    if (attachment is null)
    {
        return Results.NotFound();
    }

    return Results.File(attachment.Content, attachment.ContentType, attachment.FileName);
});

app.MapDelete("/api/tickets/{id:int}/attachments/{attachmentId:int}", async (int id, int attachmentId, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var database = await repository.LoadAsync();
    var existingTicket = database.Tickets.FirstOrDefault(ticket => ticket.Id == id);
    if (existingTicket is null || !CanViewTicket(currentUser, existingTicket))
    {
        return Results.NotFound();
    }

    var deleted = await repository.DeleteAttachmentAsync(id, attachmentId, currentUser);
    return deleted
        ? Results.Ok(new { message = "Файлът е изтрит." })
        : Results.BadRequest(new { message = "Файлът не е намерен или нямаш права да го изтриеш." });
});

app.MapPost("/api/tickets/{id:int}/feedback", async (int id, SubmitFeedbackRequest request, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);

    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var ticket = await repository.SubmitFeedbackAsync(id, currentUser, request.Rating, request.Comment);
    if (ticket is null)
    {
        return Results.BadRequest(new { message = "Обратната връзка може да бъде изпратена само веднъж за собствен затворен тикет." });
    }

    return Results.Ok(ToTicketDto(ticket, await repository.LoadAsync()));
});

app.MapGet("/api/notifications", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var notifications = await repository.GetNotificationsForUserAsync(currentUser.Id);
    return Results.Ok(notifications.Select(ToNotificationDto));
});

app.MapPost("/api/notifications/read-all", async (TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    var readCount = await repository.MarkAllNotificationsReadAsync(currentUser.Id);
    return Results.Ok(new { message = "Всички нотификации са маркирани като прочетени.", readCount });
});

app.MapPost("/api/notifications/{id:int}/read", async (int id, TicketRepository repository, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    return await repository.MarkNotificationReadAsync(id, currentUser.Id)
        ? Results.Ok(new { message = "Нотификацията е маркирана като прочетена." })
        : Results.NotFound(new { message = "Нотификацията не е намерена." });
});

app.MapGet("/api/kpi", async (TicketRepository repository, KpiService kpiService, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var database = await repository.LoadAsync();
    var agentKpis = kpiService.ComputeAgentKpis(database);
    var resolvedTickets = kpiService.GetResolvedTickets(database);

    return Results.Ok(new { agents = agentKpis, resolvedTickets });
});

app.MapGet("/api/kpi/export", async (TicketRepository repository, KpiService kpiService, HttpContext context) =>
{
    var currentUser = await GetCurrentUserAsync(repository, context);
    if (currentUser is null)
    {
        return Results.Unauthorized();
    }

    if (currentUser.Role != UserRole.Admin)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var database = await repository.LoadAsync();
    var agentKpis = kpiService.ComputeAgentKpis(database);
    var resolvedTickets = kpiService.GetResolvedTickets(database);
    var fileBytes = kpiService.GenerateExcelReport(agentKpis, resolvedTickets);
    var fileName = $"kpi-{DateTime.UtcNow:yyyy-MM-dd}.xlsx";

    return Results.File(
        fileBytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
});

app.Run();

static async Task<AppUser?> GetCurrentUserAsync(TicketRepository repository, HttpContext context)
{
    var userId = context.Session.GetInt32("UserId");
    if (userId is null)
    {
        return null;
    }

    var database = await repository.LoadAsync();
    return database.Users.FirstOrDefault(user => user.Id == userId.Value);
}

static bool CanViewTicket(AppUser user, Ticket ticket)
{
    return user.Role switch
    {
        UserRole.Admin => true,
        UserRole.Agent => ticket.AssignedAgentId == user.Id,
        UserRole.User => ticket.RequesterId == user.Id,
        _ => false
    };
}

static object ToUserDto(AppUser user)
{
    return new
    {
        user.Id,
        user.FullName,
        user.Email,
        user.Role,
        user.Specializations
    };
}

static object ToTicketDto(Ticket ticket, AppDatabase database)
{
    var requester = database.Users.FirstOrDefault(user => user.Id == ticket.RequesterId);
    var agent = database.Users.FirstOrDefault(user => user.Id == ticket.AssignedAgentId);
    var now = DateTime.UtcNow;

    return new
    {
        ticket.Id,
        ticket.Number,
        ticket.Title,
        ticket.Description,
        ticket.Category,
        ticket.SubCategory,
        ticket.Priority,
        ticket.Status,
        ticket.RequesterId,
        ticket.AssignedAgentId,
        RequesterName = requester?.FullName ?? "Неизвестен потребител",
        AgentName = agent?.FullName ?? "Няма назначен агент",
        ticket.CreatedAtUtc,
        ticket.UpdatedAtUtc,
        ticket.DueAtUtc,
        ticket.StartedAtUtc,
        ticket.ClosedAtUtc,
        ticket.ClosedByAgentId,
        IsOverdue = ticket.Status != TicketStatus.Closed && ticket.DueAtUtc < now,
        RemainingMinutes = Math.Max(0, (int)(ticket.DueAtUtc - now).TotalMinutes),
        WorkDurationMinutes = ticket.StartedAtUtc is not null && ticket.ClosedAtUtc is not null
            ? Math.Max(0, (int)(ticket.ClosedAtUtc.Value - ticket.StartedAtUtc.Value).TotalMinutes)
            : (int?)null,
        Feedback = ticket.Feedback is null
            ? null
            : new
            {
                ticket.Feedback.UserId,
                ticket.Feedback.Rating,
                ticket.Feedback.Comment,
                ticket.Feedback.CreatedAtUtc
            },
        Comments = ticket.Comments.Select(comment =>
        {
            var author = database.Users.FirstOrDefault(user => user.Id == comment.AuthorId);
            return new
            {
                comment.Id,
                comment.AuthorId,
                AuthorName = author?.FullName ?? "Неизвестен потребител",
                comment.Text,
                comment.CreatedAtUtc
            };
        }),
        Attachments = ticket.Attachments.Select(attachment =>
        {
            var uploader = database.Users.FirstOrDefault(user => user.Id == attachment.UploadedByUserId);
            return ToAttachmentDto(attachment, uploader?.FullName ?? "Неизвестен потребител");
        })
    };
}

static object ToAttachmentDto(TicketAttachment attachment, string uploaderName)
{
    return new
    {
        attachment.Id,
        attachment.FileName,
        attachment.ContentType,
        attachment.SizeBytes,
        attachment.UploadedByUserId,
        UploadedByName = uploaderName,
        attachment.UploadedAtUtc
    };
}

static object ToAbsenceDto(Absence absence, AppDatabase database)
{
    var agent = database.Users.FirstOrDefault(user => user.Id == absence.AgentId);
    var requestedBy = database.Users.FirstOrDefault(user => user.Id == absence.RequestedByUserId);
    var decidedBy = absence.DecidedByUserId is null
        ? null
        : database.Users.FirstOrDefault(user => user.Id == absence.DecidedByUserId);

    return new
    {
        absence.Id,
        absence.AgentId,
        AgentName = agent?.FullName ?? "Неизвестен агент",
        absence.StartDate,
        absence.EndDate,
        absence.Type,
        absence.Reason,
        absence.Status,
        absence.RequestedByUserId,
        RequestedByName = requestedBy?.FullName ?? "Неизвестен потребител",
        absence.DecidedByUserId,
        DecidedByName = decidedBy?.FullName,
        absence.CreatedAtUtc,
        absence.DecidedAtUtc
    };
}

static object ToNotificationDto(TicketNotification notification)
{
    return new
    {
        notification.Id,
        notification.TicketId,
        notification.Message,
        Type = string.IsNullOrWhiteSpace(notification.Type) ? "OpenTicket" : notification.Type,
        notification.CreatedAtUtc,
        notification.IsRead
    };
}

public sealed record LoginRequest(string Email, string Password);

public sealed record CreateAgentRequest(string Username, string Password);

public sealed record UpdateSpecializationsRequest(List<string> Specializations);

public sealed record CreateTicketRequest(
    string Title,
    string Description,
    string Category,
    string SubCategory,
    TicketPriority Priority);

public sealed record CreateAbsenceRequest(DateOnly StartDate, DateOnly EndDate, AbsenceType Type, string? Reason);

public sealed record CreateAbsenceForAgentRequest(int AgentId, DateOnly StartDate, DateOnly EndDate, AbsenceType Type, string? Reason);

public sealed record DecideAbsenceRequest(bool Approve);

public sealed record ReassignTicketRequest(int AgentId);

public sealed record AddCommentRequest(string Text);

public sealed record SubmitFeedbackRequest(int Rating, string Comment);
