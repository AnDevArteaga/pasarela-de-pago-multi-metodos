using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Pasarela.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Origin> Origins => Set<Origin>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<Acceptance> Acceptances => Set<Acceptance>();
    public DbSet<PaymentSource> PaymentSources => Set<PaymentSource>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Origin>().ToTable("origins");
        modelBuilder.Entity<Product>().ToTable("products");
        modelBuilder.Entity<CheckoutSession>().ToTable("checkout_sessions");
        modelBuilder.Entity<Acceptance>().ToTable("acceptances");
        modelBuilder.Entity<PaymentSource>().ToTable("payment_sources");
        modelBuilder.Entity<Subscription>().ToTable("subscriptions");
        modelBuilder.Entity<Payment>().ToTable("payments");
        modelBuilder.Entity<WebhookEvent>().ToTable("webhook_events");
        modelBuilder.Entity<OutboxMessage>().ToTable("outbox");

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
            }
        }

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Origin)
            .WithMany()
            .HasForeignKey(p => p.OriginId);

        modelBuilder.Entity<CheckoutSession>()
            .HasOne(s => s.Product)
            .WithMany()
            .HasForeignKey(s => s.ProductId);

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.Product)
            .WithMany()
            .HasForeignKey(s => s.ProductId);

        modelBuilder.Entity<Subscription>()
            .HasOne(s => s.PaymentSource)
            .WithMany()
            .HasForeignKey(s => s.PaymentSourceId);

        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Product)
            .WithMany()
            .HasForeignKey(p => p.ProductId);

        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Subscription)
            .WithMany()
            .HasForeignKey(p => p.SubscriptionId);
    }

    public static async Task ApplySchemaAsync(AppDb db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using (var create = conn.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                  id text PRIMARY KEY,
                  applied_at timestamptz NOT NULL DEFAULT now()
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE id = '001'";
            if (await check.ExecuteScalarAsync(ct) != null) return;
        }

        foreach (var statement in SplitSql(ReadSql()))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO schema_migrations (id) VALUES ('001') ON CONFLICT (id) DO NOTHING";
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<string> SplitSql(string sql)
    {
        var chunk = new StringBuilder();
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
            chunk.AppendLine(line);
            if (trimmed.EndsWith(';'))
            {
                var statement = chunk.ToString().Trim().TrimEnd(';').Trim();
                chunk.Clear();
                if (statement.Length > 0) yield return statement;
            }
        }

        var tail = chunk.ToString().Trim().TrimEnd(';').Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static string ReadSql()
    {
        var assembly = typeof(AppDb).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("001_init.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Falta 001_init.sql");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
