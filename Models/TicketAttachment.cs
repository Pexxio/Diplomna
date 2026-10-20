namespace TicketSystem.Models;

//прикачен файл към тикет - пази се и след затваряне на тикета; трие се само ръчно или заедно с тикета (каскадно)
public sealed class TicketAttachment
{
    public int Id { get; set; }

    public int TicketId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public byte[] Content { get; set; } = Array.Empty<byte>();

    public int UploadedByUserId { get; set; }

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}
