namespace TicketSystem.Models;

//резултат от опит за качване на прикачен файл
public enum AddAttachmentResult
{
    Success,
    TicketNotFound,
    TicketClosed,
    FileTooLarge,
    DisallowedFileType,
    TooManyAttachments
}
