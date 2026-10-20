namespace TicketSystem.Models;

//скорошна двойка категория/подкатегория от историята на потребителя
public sealed class RecentCategory
{
    public string TopCategory { get; set; } = string.Empty;

    public string SubCategory { get; set; } = string.Empty;
}
