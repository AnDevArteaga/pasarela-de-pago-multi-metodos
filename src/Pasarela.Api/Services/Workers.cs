using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasarela.Data;
using Pasarela.Domain;

namespace Pasarela.Services;

public sealed class OutboxWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpFactory,
    AppSettings settings,
    ILogger<OutboxWorker> log) : BackgroundService
{
    private static readonly int[] BackoffMinutes = [1, 5, 30, 120, 360];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "El outbox falló en este ciclo");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        if (!await Locks.TryAsync(db, 71001, ct)) return;
        try
        {
            var due = await db.Outbox
                .Where(o => o.Status == "pending" && o.NextAttemptAt <= DateTimeOffset.UtcNow)
                .OrderBy(o => o.NextAttemptAt)
                .Take(20)
                .ToListAsync(ct);
            foreach (var message in due)
            {
                await SendOneAsync(db, message, ct);
            }
        }
        finally
        {
            await Locks.UnlockAsync(db, 71001, ct);
        }
    }

    private async Task SendOneAsync(AppDb db, OutboxMessage message, CancellationToken ct)
    {
        try
        {
            if (message.Type == "send_email") await SendEmailAsync(db, message, ct);
            else await NotifyAsync(db, message, ct);
            message.Status = "sent";
            message.SentAt = DateTimeOffset.UtcNow;
            message.LastError = null;
            log.LogInformation("Outbox {Type} {EventKey} enviado", message.Type, message.EventKey);
        }
        catch (Exception ex)
        {
            message.Attempts += 1;
            message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            if (message.Attempts >= 8)
            {
                message.Status = "failed";
                log.LogError(ex, "Alerta: outbox {EventKey} agotó los reintentos", message.EventKey);
            }
            else
            {
                var minutes = BackoffMinutes[Math.Min(message.Attempts - 1, BackoffMinutes.Length - 1)];
                message.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
                log.LogWarning(ex, "Outbox {EventKey} reintento {Attempt}", message.EventKey, message.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SendEmailAsync(AppDb db, OutboxMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ResendApiKey))
        {
            throw new InvalidOperationException("Falta RESEND_API_KEY.");
        }

        if (message.PaymentId is null) throw new InvalidOperationException("El correo no tiene pago.");
        var payment = await db.Payments.Include(p => p.Product).ThenInclude(p => p.Origin)
            .FirstAsync(p => p.Id == message.PaymentId, ct);
        using var payload = JsonDocument.Parse(message.Payload);
        var kind = payload.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var html = kind == "source_invalid"
            ? EmailHtml.CardUpdate(payment.Product.Name)
            : EmailHtml.Render(payment, payment.Product);
        var subject = kind == "source_invalid"
            ? $"Actualiza tu tarjeta — {payment.Product.Name}"
            : EmailHtml.Subject(payment.Status, payment.Product.Name);
        var from = string.IsNullOrWhiteSpace(payment.Product.Origin.MailFrom)
            ? "Pagos Hernan Elejalde Amin <pagos@example.com>"
            : payment.Product.Origin.MailFrom;

        var body = JsonSerializer.Serialize(new
        {
            from,
            to = new[] { payment.PayerEmail },
            subject,
            html,
        });
        var http = httpFactory.CreateClient("resend");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResendApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Resend {(int)response.StatusCode}: {raw}");
        }
    }

    private async Task NotifyAsync(AppDb db, OutboxMessage message, CancellationToken ct)
    {
        Product? product = null;
        if (message.PaymentId is not null)
        {
            product = await db.Payments.Where(p => p.Id == message.PaymentId).Select(p => p.Product).FirstAsync(ct);
        }
        else if (message.SubscriptionId is not null)
        {
            product = await db.Subscriptions.Where(s => s.Id == message.SubscriptionId).Select(s => s.Product).FirstAsync(ct);
        }
        if (product is null || string.IsNullOrWhiteSpace(product.NotifyUrl))
        {
            throw new InvalidOperationException("El producto no tiene URL de aviso.");
        }

        await db.Entry(product).Reference(p => p.Origin).LoadAsync(ct);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Signatures.HmacHex(product.Origin.NotifySecret, Signatures.SignedBody(timestamp, message.Payload));
        var http = httpFactory.CreateClient("notify");
        using var request = new HttpRequestMessage(HttpMethod.Post, product.NotifyUrl);
        request.Content = new StringContent(message.Payload, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("x-signature", signature);
        request.Headers.TryAddWithoutValidation("x-timestamp", timestamp.ToString());
        request.Headers.TryAddWithoutValidation("x-webhook-secret", product.Origin.NotifySecret);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Aviso {(int)response.StatusCode} hacia {product.NotifyUrl}");
        }
    }
}

public sealed class ReconcileWorker(IServiceScopeFactory scopes, ILogger<ReconcileWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "El reconciliador falló en este ciclo");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var wompi = scope.ServiceProvider.GetRequiredService<WompiClient>();
        var transitions = scope.ServiceProvider.GetRequiredService<TransitionService>();
        var charges = scope.ServiceProvider.GetRequiredService<ChargeService>();
        if (!await Locks.TryAsync(db, 71002, ct)) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
            var pending = await db.Payments
                .Where(p => p.Status == PaymentStates.Pending && p.CreatedAt <= cutoff)
                .OrderBy(p => p.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            foreach (var payment in pending)
            {
                var age = DateTimeOffset.UtcNow - payment.CreatedAt;
                if (string.IsNullOrWhiteSpace(payment.WompiTransactionId))
                {
                    var found = await wompi.FindByReferenceAsync(payment.WompiReference, ct);
                    if (found is null)
                    {
                        if (age > TimeSpan.FromMinutes(2) && payment.SubscriptionId is not null)
                        {
                            await charges.SubmitExistingAsync(payment, ct);
                        }
                        else if (age > TimeSpan.FromMinutes(30))
                        {
                            await transitions.TransitionAsync(payment.Id, PaymentStates.Error, "reconcile", "sin transacción", ct);
                        }
                        continue;
                    }
                    payment.WompiTransactionId = found.Id;
                    payment.AsyncPaymentUrl ??= found.AsyncPaymentUrl;
                    await db.SaveChangesAsync(ct);
                }

                var remote = await wompi.GetAsync(payment.WompiTransactionId!, ct);
                if (remote is null) continue;
                payment.AsyncPaymentUrl ??= remote.AsyncPaymentUrl;
                var status = PaymentStates.Normalize(remote.Status);
                if (status == PaymentStates.Pending && Expired(payment, age))
                {
                    await transitions.TransitionAsync(payment.Id, PaymentStates.Error, "reconcile", "expirado", ct);
                    continue;
                }
                if (status != PaymentStates.Pending)
                {
                    await transitions.TransitionAsync(payment.Id, status, "reconcile", remote.StatusMessage, ct);
                }
                else
                {
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        finally
        {
            await Locks.UnlockAsync(db, 71002, ct);
        }
    }

    private static bool Expired(Payment payment, TimeSpan age) => payment.Method switch
    {
        "PSE" or "BANCOLOMBIA_TRANSFER" => age >= TimeSpan.FromHours(24),
        "NEQUI" => age >= TimeSpan.FromMinutes(15),
        "CARD" => age >= TimeSpan.FromMinutes(30),
        _ => age >= TimeSpan.FromHours(24),
    };
}

public sealed class BillingWorker(IServiceScopeFactory scopes, ILogger<BillingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BillAsync(stoppingToken);
                await ExpireAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "El cron de suscripciones falló en este ciclo");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private async Task BillAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var charges = scope.ServiceProvider.GetRequiredService<ChargeService>();
        if (!await Locks.TryAsync(db, 71003, ct)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var due = await db.Subscriptions
                .Include(s => s.Product)
                .Include(s => s.PaymentSource)
                .Where(s => (s.Status == "active" || s.Status == "past_due")
                    && !s.CancelAtPeriodEnd
                    && s.CancelReason != "source_invalid"
                    && (s.NextRetryAt ?? s.NextBillingAt) <= now)
                .OrderBy(s => s.NextBillingAt)
                .Take(30)
                .ToListAsync(ct);

            foreach (var sub in due)
            {
                var attempt = sub.AttemptCount + 1;
                var period = DateOnly.FromDateTime((sub.CurrentPeriodEnd ?? now).UtcDateTime);
                var key = $"renew:{sub.Id:N}:{period:yyyyMMdd}:{attempt}";
                if (await db.Payments.AnyAsync(p => p.IdempotencyKey == key, ct)) continue;
                if (sub.PaymentSource is null)
                {
                    sub.Status = "past_due";
                    sub.CancelReason = "source_invalid";
                    sub.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    PublicId = Guid.NewGuid(),
                    OriginId = sub.OriginId,
                    ProductId = sub.ProductId,
                    SubscriptionId = sub.Id,
                    Ref = sub.Ref,
                    AmountCents = sub.PriceCents,
                    Method = "CARD",
                    Status = PaymentStates.Pending,
                    IdempotencyKey = key,
                    AttemptNumber = attempt,
                    PeriodStart = period,
                    PayerEmail = sub.PaymentSource.CustomerEmail,
                    CardBrand = sub.PaymentSource.Brand,
                    CardLastFour = sub.PaymentSource.LastFour,
                    FirstPeriod = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                payment.WompiReference = $"sub{sub.Id:N}-{period:yyyyMMdd}-a{attempt}";
                db.Payments.Add(payment);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    db.Entry(payment).State = EntityState.Detached;
                    log.LogInformation("Otro proceso ya creó el cobro {Key}", key);
                    continue;
                }

                log.LogInformation("Renovación {SubscriptionId} intento {Attempt}", sub.Id, attempt);
                await charges.SubmitExistingAsync(payment, ct);
            }
        }
        finally
        {
            await Locks.UnlockAsync(db, 71003, ct);
        }
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var transitions = scope.ServiceProvider.GetRequiredService<TransitionService>();
        if (!await Locks.TryAsync(db, 71004, ct)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var rows = await db.Subscriptions
                .Include(s => s.Product)
                .ThenInclude(p => p.Origin)
                .Where(s => !s.AccessEndNotified && s.CurrentPeriodEnd != null &&
                    (s.Status == "past_due" || s.Status == "canceled" || (s.Status == "active" && s.CancelAtPeriodEnd)))
                .Take(50)
                .ToListAsync(ct);

            foreach (var sub in rows)
            {
                var grace = sub.Status == "past_due" ? sub.Product.GraceDays : 0;
                if (sub.CurrentPeriodEnd!.Value.AddDays(grace) > now) continue;
                await transitions.NotifySubscriptionAsync(sub, sub.Product, sub.Product.Origin, "inactive", "subscription.expired", null, ct);
                sub.AccessEndNotified = true;
                if (sub.Status == "past_due") sub.Status = "expired";
                sub.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                log.LogInformation("Suscripción {SubscriptionId} avisó fin de acceso", sub.Id);
            }
        }
        finally
        {
            await Locks.UnlockAsync(db, 71004, ct);
        }
    }
}

public static class Locks
{
    public static async Task<bool> TryAsync(AppDb db, int key, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
        cmd.Parameters.AddWithValue("k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool locked && locked;
    }

    public static async Task UnlockAsync(AppDb db, int key, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) return;
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteScalarAsync(ct);
    }
}
