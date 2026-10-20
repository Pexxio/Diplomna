namespace TicketSystem.Models;

//домейн модел за тикетите
public sealed class Ticket
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "Общо";

    public string SubCategory { get; set; } = string.Empty;

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public int RequesterId { get; set; }

    public int? AssignedAgentId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime DueAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public int? ClosedByAgentId { get; set; }

    public TicketFeedback? Feedback { get; set; }

    public List<TicketComment> Comments { get; set; } = new();

    public List<TicketAttachment> Attachments { get; set; } = new();
}
