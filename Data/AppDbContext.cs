using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TicketSystem.Models;

namespace TicketSystem.Data;

//EF Core контекст - замества App_Data/database.json с PostgreSQL
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketComment> Comments => Set<TicketComment>();

    public DbSet<TicketAttachment> Attachments => Set<TicketAttachment>();

    public DbSet<TicketNotification> Notifications => Set<TicketNotification>();

    public DbSet<Absence> Absences => Set<Absence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(user => user.Role).HasConversion<string>();

            //списъкът със специализации се пази като нативен Postgres text[]
            entity.Property(user => user.Specializations)
                .HasColumnType("text[]")
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    v => v.ToList()));
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.Property(ticket => ticket.Priority).HasConversion<string>();
            entity.Property(ticket => ticket.Status).HasConversion<string>();
            entity.HasIndex(ticket => ticket.Number).IsUnique();

            entity.HasMany(ticket => ticket.Comments)
                .WithOne()
                .HasForeignKey(comment => comment.TicketId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(ticket => ticket.Attachments)
                .WithOne()
                .HasForeignKey(attachment => attachment.TicketId)
                .OnDelete(DeleteBehavior.Cascade);

            //обратната връзка е незадължителен 1:1 обект, вграден в реда на тикета
            entity.OwnsOne(ticket => ticket.Feedback, feedback =>
            {
                feedback.Property(item => item.UserId).HasColumnName("Feedback_UserId");
                feedback.Property(item => item.Rating).HasColumnName("Feedback_Rating");
                feedback.Property(item => item.Comment).HasColumnName("Feedback_Comment");
                feedback.Property(item => item.CreatedAtUtc).HasColumnName("Feedback_CreatedAtUtc");
            });
        });

        modelBuilder.Entity<Absence>(entity =>
        {
            entity.Property(item => item.Type).HasConversion<string>();
            entity.Property(item => item.Status).HasConversion<string>();
        });
    }
}
