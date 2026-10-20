namespace TicketSystem.Models;

//тук се пазят коментарите
public sealed class TicketComment
{
    public int Id { get; set; }

    //FK към родителския тикет (нужен за релационното съхранение)
    public int TicketId { get; set; }

    public int AuthorId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
