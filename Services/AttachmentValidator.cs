namespace TicketSystem.Services;

//чисти правила за валидация на прикачени файлове - размер и разрешени разширения
public static class AttachmentValidator
{
    //малък лимит, но достатъчен за нормален screenshot (дори 4K)
    public const long MaxFileSizeBytes = 3 * 1024 * 1024;

    public const int MaxAttachmentsPerTicket = 5;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
        ".pdf", ".docx", ".xlsx", ".txt", ".log"
    };

    //проверява по разширение, не по декларирания от клиента Content-Type (той е лесен за подправяне)
    public static bool IsAllowedFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && AllowedExtensions.Contains(extension);
    }

    public static bool IsWithinSizeLimit(long sizeBytes)
    {
        return sizeBytes > 0 && sizeBytes <= MaxFileSizeBytes;
    }
}
