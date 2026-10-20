namespace TicketSystem.Models;

/// <summary>
/// Модел за потребител в системата.
/// </summary>
public sealed class AppUser
{
    public int Id { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    //BCrypt хеш, не суров текст - виж DbSeeder.Hash() и /api/login в Program.cs
    public string Password { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    //категории, в които агентът е специализиран (използва се само за Role == Agent)
    public List<string> Specializations { get; set; } = new();
}
