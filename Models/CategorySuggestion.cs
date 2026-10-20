namespace TicketSystem.Models;

//предложение за категория, изведено от ключови думи в заглавието на тикета
public sealed class CategorySuggestion
{
    public string TopCategory { get; set; } = string.Empty;

    public string SubCategory { get; set; } = string.Empty;

    public double Score { get; set; }
}
