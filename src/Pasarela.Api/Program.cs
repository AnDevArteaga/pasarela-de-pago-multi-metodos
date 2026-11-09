using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Pasarela.Data;
using Pasarela.Endpoints;
using Pasarela.Services;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var settings = new AppSettings
{
    EnvironmentName = builder.Configuration["APP_ENV"] ?? "test",
    PublicBaseUrl = builder.Configuration["PUBLIC_BASE_URL"] ?? "",
    AdminToken = builder.Configuration["ADMIN_TOKEN"] ?? "",
    ResendApiKey = builder.Configuration["RESEND_API_KEY"] ?? "",
    RequestsPerMinute = Parse(builder.Configuration["RATE_LIMIT_REQUESTS_PER_MINUTE"], 10),
    FailedChargesPerHour = Parse(builder.Configuration["RATE_LIMIT_FAILED_CHARGES_PER_HOUR"], 5),
};
var wompiOptions = new WompiOptions
{
    PublicKey = builder.Configuration["WOMPI_PUBLIC_KEY"] ?? "",
    PrivateKey = builder.Configuration["WOMPI_PRIVATE_KEY"] ?? "",
    IntegritySecret = builder.Configuration["WOMPI_INTEGRITY_SECRET"] ?? "",
    EventsSecret = builder.Configuration["WOMPI_EVENTS_SECRET"] ?? "",
};

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var trusted = builder.Configuration["TRUSTED_PROXIES"] ?? "";
    if (trusted.Equals("railway", StringComparison.OrdinalIgnoreCase) || trusted == "*")
    {
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(wompiOptions);
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddHttpClient("wompi");
builder.Services.AddHttpClient("resend");
builder.Services.AddHttpClient("notify");
builder.Services.AddDbContext<AppDb>(options =>
    options.UseNpgsql(PostgresUrl.Normalize(builder.Configuration["DATABASE_URL"] ?? "Host=localhost;Username=pasarela;Password=pasarela;Database=pasarela")));
builder.Services.AddHttpClient<WompiClient>();
builder.Services.AddScoped<TransitionService>();
builder.Services.AddScoped<ChargeService>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<ReconcileWorker>();
builder.Services.AddHostedService<BillingWorker>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
HttpApi.Map(app);
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await AppDb.ApplySchemaAsync(db, CancellationToken.None);
    if (string.Equals(builder.Configuration["SEED_DEMO"], "true", StringComparison.OrdinalIgnoreCase)
        && !await db.Origins.AnyAsync())
    {
        var origin = new Origin
        {
            Name = "Demo",
            SigningSecret = "dev-signing-secret",
            NotifySecret = "dev-notify-secret",
            MailFrom = "Pagos Hernan Elejalde Amin <pagos@example.com>",
            Active = true,
        };
        db.Origins.Add(origin);
        await db.SaveChangesAsync();
        db.Products.AddRange(
            new Product
            {
                OriginId = origin.Id,
                Name = "Plan mensual",
                Description = "Suscripción de prueba",
                PriceCents = 5_000_000,
                Type = "subscription",
                NotifyKind = "subscription",
                BillingInterval = "month",
                IntervalCount = 1,
                MaxAttempts = 3,
                RetryDays = "1,3,5",
                GraceDays = 3,
                Active = true,
            },
            new Product
            {
                OriginId = origin.Id,
                Name = "Consulta",
                Description = "Pago único de prueba",
                PriceCents = 9_500_000,
                Type = "one_time",
                NotifyKind = "one_time",
                Active = true,
            });
        await db.SaveChangesAsync();
        app.Logger.LogInformation("Semilla demo creada. Origen {OriginId}. Firma dev-signing-secret", origin.Id);
    }
}

app.Run();

static int Parse(string? raw, int fallback) =>
    int.TryParse(raw, out var value) && value > 0 ? value : fallback;

public static class PostgresUrl
{
    public static string Normalize(string url)
    {
        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var uri = new Uri(url);
        var user = uri.UserInfo.Split(':', 2);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(user[0]),
            Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
            Database = uri.AbsolutePath.Trim('/'),
        };
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        string? ssl = null;
        foreach (var pair in query)
        {
            var bits = pair.Split('=', 2);
            if (bits.Length == 2 && bits[0] == "sslmode") ssl = Uri.UnescapeDataString(bits[1]);
        }
        builder.SslMode = ssl switch
        {
            "disable" => Npgsql.SslMode.Disable,
            "require" => Npgsql.SslMode.Require,
            _ => uri.Host is "localhost" or "postgres" or "db" ? Npgsql.SslMode.Disable : Npgsql.SslMode.Require,
        };
        return builder.ConnectionString;
    }
}
