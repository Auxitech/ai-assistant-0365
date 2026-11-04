using Microsoft.EntityFrameworkCore;

namespace MailRouter.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<RoutingRule> Rules => Set<RoutingRule>();
    public DbSet<ProcessedMessage> Messages => Set<ProcessedMessage>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProcessedMessage>().HasIndex(m => new { m.Source, m.ExternalId }).IsUnique();
        b.Entity<ProcessedMessage>().HasIndex(m => m.ProcessedAt);
        b.Entity<ProcessedMessage>().HasIndex(m => m.ConversationId);

        // SQLite can't ORDER BY DateTimeOffset natively; store as ticks.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (prop.ClrType == typeof(DateTimeOffset))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            else if (prop.ClrType == typeof(DateTimeOffset?))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }

        b.Entity<AppSettings>().HasData(new AppSettings { Id = 1 });
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await Settings.FirstOrDefaultAsync(s => s.Id == 1, ct)
        ?? (await Settings.AddAsync(new AppSettings { Id = 1 }, ct)).Entity;
}
