using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pasarela.Data;
using Pasarela.Domain;
using Pasarela.Services;

namespace Pasarela.Endpoints;

public static class HttpApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", async (AppDb db) =>
        {
            try
            {
                if (!await db.Database.CanConnectAsync()) return Results.StatusCode(503);
                return Results.Ok(new { ok = true });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        });

        app.MapPost("/api/checkout/sessions", CreateSession);
        app.MapGet("/api/checkout/sessions/{id:guid}", GetSession);
        app.MapGet("/api/checkout/sessions/{id:guid}/banks", Banks);
        app.MapPost("/api/checkout/sessions/{id:guid}/payments", Pay);
        app.MapPost("/api/checkout/sessions/{id:guid}/card", ChangeCard);
        app.MapGet("/api/payments/{publicId:guid}", Voucher);
        app.MapGet("/api/payments/{publicId:guid}/async", AsyncUrl);
        app.MapGet("/api/wompi/return", WompiReturn);
        app.MapPost("/api/webhooks/wompi", WompiWebhook);
        app.MapPost("/api/subscriptions/{id:guid}/cancel", CancelServer);
        app.MapPost("/api/subscriptions/{id:guid}/reactivate", ReactivateServer);
        app.MapPost("/api/public/cancel", CancelFromPage);
        app.MapPost("/api/admin/origins", CreateOrigin);
        app.MapPost("/api/admin/products", CreateProduct);
        app.MapGet("/api/admin/products", ListProducts);
        app.MapGet("/api/dev/sign", DevSign);
    }

    private static async Task<IResult> CreateSession(CreateSessionInput input, AppDb db, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Origin).FirstOrDefaultAsync(p => p.Id == input.ProductId && p.Active, ct);
        if (product is null) return Results.NotFound(new { error = "Producto no encontrado." });

        var signed = false;
        string? reference = null;
        if (!string.IsNullOrWhiteSpace(input.Sig))
        {
            if (input.Ts is null || string.IsNullOrWhiteSpace(input.Ref))
            {
                return Results.BadRequest(new { error = "La firma necesita ref y ts." });
            }
            if (!Signatures.TimestampFresh(input.Ts.Value, DateTimeOffset.UtcNow))
            {
                return Results.BadRequest(new { error = "La firma venció." });
            }
            var expected = Signatures.HmacHex(
                product.Origin.SigningSecret,
                Signatures.CheckoutPayload(product.Id, input.Ref, input.Ts.Value));
            if (!Signatures.FixedEquals(expected, input.Sig))
            {
                return Results.BadRequest(new { error = "La firma no coincide." });
            }
            signed = true;
            reference = input.Ref.Trim();
        }

        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            OriginId = product.OriginId,
            Ref = reference,
            Signed = signed,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { sessionId = session.Id });
    }

    private static async Task<IResult> GetSession(Guid id, AppDb db, WompiOptions wompi, CancellationToken ct)
    {
        var session = await db.CheckoutSessions.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound(new { error = "La sesión no existe." });
        if (session.ExpiresAt < DateTimeOffset.UtcNow) return Results.StatusCode(410);
        var product = session.Product;
        PaymentSource? card = null;
        if (session.Signed && session.Ref != null)
        {
            card = await db.PaymentSources
                .Where(s => s.OriginId == session.OriginId && s.Ref == session.Ref && s.Type == "CARD" && s.Status == "AVAILABLE")
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }

        var next = BillingMath.AddInterval(DateTimeOffset.UtcNow, product.BillingInterval, product.IntervalCount);
        return Results.Ok(new
        {
            sessionId = session.Id,
            signed = session.Signed,
            publicKey = wompi.PublicKey,
            product = ProductView(product),
            nextChargeAt = product.Type == "subscription" ? next : (DateTimeOffset?)null,
            legal = new
            {
                privacyVersion = LegalCopy.PrivacyVersion,
                privacyText = LegalCopy.PrivacyText,
                recurringVersion = LegalCopy.RecurringVersion,
                recurringText = LegalCopy.RecurringText(product.Name, product.PriceCents, product.BillingInterval, product.IntervalCount),
            },
            savedCard = card == null ? null : new { id = card.Id, brand = card.Brand, lastFour = card.LastFour },
        });
    }

    private static async Task<IResult> Banks(Guid id, AppDb db, WompiClient wompi, CancellationToken ct)
    {
        var session = await OpenSession(db, id, ct);
        if (session is null) return Results.NotFound(new { error = "La sesión no existe." });
        var banks = await wompi.BanksAsync(ct);
        return Results.Ok(banks.Select(b => new { code = b.Code, name = b.Name }));
    }

    private static async Task<IResult> Pay(Guid id, ChargeInput input, HttpContext http, ChargeService charges, AppSettings settings, CancellationToken ct)
    {
        var (status, body) = await charges.ChargeAsync(id, input, ClientIp(http), BaseUrl(http, settings), ct);
        return Results.Json(body, statusCode: status);
    }

    private static async Task<IResult> Voucher(Guid publicId, AppDb db, WompiClient wompi, TransitionService transitions, CancellationToken ct)
    {
        var payment = await db.Payments.Include(p => p.Product).FirstOrDefaultAsync(p => p.PublicId == publicId, ct);
        if (payment is null) return Results.NotFound(new { error = "No encontramos el pago." });
        if (payment.Status == PaymentStates.Pending && !string.IsNullOrWhiteSpace(payment.WompiTransactionId))
        {
            var remote = await wompi.GetAsync(payment.WompiTransactionId, ct);
            if (remote != null)
            {
                payment.AsyncPaymentUrl ??= remote.AsyncPaymentUrl;
                var status = PaymentStates.Normalize(remote.Status);
                if (status != PaymentStates.Pending)
                {
                    await transitions.TransitionAsync(payment.Id, status, "voucher", remote.StatusMessage, ct);
                    await db.Entry(payment).ReloadAsync(ct);
                }
            }
        }

        return Results.Ok(new
        {
            status = payment.Status,
            product = payment.Product.Name,
            amountCents = payment.AmountCents,
            method = payment.Method,
            reference = payment.WompiReference,
            createdAt = payment.CreatedAt,
            email = MailMask.Mask(payment.PayerEmail),
        });
    }

    private static async Task<IResult> AsyncUrl(Guid publicId, AppDb db, WompiClient wompi, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.PublicId == publicId, ct);
        if (payment is null) return Results.NotFound(new { error = "No encontramos el pago." });
        if (!string.IsNullOrWhiteSpace(payment.AsyncPaymentUrl))
        {
            return Results.Ok(new { url = payment.AsyncPaymentUrl, status = payment.Status });
        }
        if (string.IsNullOrWhiteSpace(payment.WompiTransactionId))
        {
            return Results.Ok(new { url = (string?)null, status = payment.Status });
        }
        var remote = await wompi.GetAsync(payment.WompiTransactionId, ct);
        if (!string.IsNullOrWhiteSpace(remote?.AsyncPaymentUrl))
        {
            payment.AsyncPaymentUrl = remote.AsyncPaymentUrl;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { url = payment.AsyncPaymentUrl, status = remote?.Status ?? payment.Status });
    }

    private static async Task<IResult> WompiReturn(string? id, AppDb db)
    {
        if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "Falta el id de Wompi." });
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.WompiTransactionId == id);
        if (payment is null) return Results.NotFound(new { error = "No encontramos esa transacción." });
        return Results.Redirect($"/voucher?pid={payment.PublicId}");
    }

    private static async Task<IResult> WompiWebhook(HttpRequest request, AppDb db, WompiOptions options, TransitionService transitions, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("WompiWebhook");
        var raw = await ReadBody(request);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            return Results.BadRequest(new { error = "JSON inválido." });
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("signature", out var signature) ||
                !signature.TryGetProperty("checksum", out var checksumEl) ||
                !signature.TryGetProperty("properties", out var propertiesEl) ||
                !root.TryGetProperty("timestamp", out var timestampEl) ||
                !root.TryGetProperty("data", out var data))
            {
                return Results.BadRequest(new { error = "Evento incompleto." });
            }

            var timestamp = timestampEl.ValueKind == JsonValueKind.Number
                ? timestampEl.GetInt64()
                : long.Parse(timestampEl.GetString() ?? "0", CultureInfo.InvariantCulture);
            var properties = propertiesEl.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToArray();
            var expected = WompiChecksum.Build(data, properties, timestamp, options.EventsSecret);
            var checksum = checksumEl.GetString() ?? "";
            if (!Signatures.FixedEquals(expected, checksum))
            {
                log.LogWarning("Checksum de Wompi no coincide");
                return Results.Unauthorized();
            }

            var transaction = data.GetProperty("transaction");
            var txId = transaction.GetProperty("id").ValueKind == JsonValueKind.String
                ? transaction.GetProperty("id").GetString() ?? ""
                : transaction.GetProperty("id").GetRawText();
            var status = transaction.GetProperty("status").GetString() ?? "PENDING";
            var eventKey = $"{txId}:{timestamp}:{checksum}";
            db.WebhookEvents.Add(new WebhookEvent
            {
                EventKey = eventKey,
                Payload = raw,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                log.LogInformation("Evento Wompi duplicado {EventKey}", eventKey);
                return Results.Ok(new { ok = true, duplicate = true });
            }

            var reference = transaction.TryGetProperty("reference", out var referenceEl) ? referenceEl.GetString() : null;
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.WompiTransactionId == txId, ct)
                ?? (reference == null ? null : await db.Payments.FirstOrDefaultAsync(p => p.WompiReference == reference, ct));
            if (payment is null)
            {
                log.LogInformation("Evento Wompi sin pago local {Tx}", txId);
                return Results.Ok(new { ok = true });
            }

            if (string.IsNullOrWhiteSpace(payment.WompiTransactionId))
            {
                payment.WompiTransactionId = txId;
                await db.SaveChangesAsync(ct);
            }

            await transitions.TransitionAsync(payment.Id, status, "webhook", null, ct);
            return Results.Ok(new { ok = true });
        }
    }

    private static async Task<IResult> CancelServer(Guid id, HttpRequest request, AppDb db, TransitionService transitions, CancellationToken ct)
    {
        var raw = await ReadBody(request);
        var sub = await db.Subscriptions.Include(s => s.Product).ThenInclude(p => p.Origin).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is null) return Results.NotFound(new { error = "Suscripción no encontrada." });
        if (!ValidBodySignature(request, raw, sub.Product.Origin.SigningSecret))
        {
            return Results.Unauthorized();
        }
        var immediate = false;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("immediate", out var flag) && flag.ValueKind == JsonValueKind.True) immediate = true;
        }
        return await ApplyCancel(db, transitions, sub, immediate, ct);
    }

    private static async Task<IResult> ReactivateServer(Guid id, HttpRequest request, AppDb db, TransitionService transitions, CancellationToken ct)
    {
        var raw = await ReadBody(request);
        var sub = await db.Subscriptions.Include(s => s.Product).ThenInclude(p => p.Origin).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is null) return Results.NotFound(new { error = "Suscripción no encontrada." });
        if (!ValidBodySignature(request, raw, sub.Product.Origin.SigningSecret)) return Results.Unauthorized();
        if (!sub.CancelAtPeriodEnd || sub.CurrentPeriodEnd < DateTimeOffset.UtcNow)
        {
            return Results.BadRequest(new { error = "Esta suscripción no se puede reactivar." });
        }
        sub.CancelAtPeriodEnd = false;
        sub.CanceledAt = null;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await transitions.NotifySubscriptionAsync(sub, sub.Product, sub.Product.Origin, "active", "subscription.renewed", null, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true, status = sub.Status });
    }

    private static async Task<IResult> CancelFromPage(PageCancelInput input, AppDb db, TransitionService transitions, CancellationToken ct)
    {
        var sub = await db.Subscriptions.Include(s => s.Product).ThenInclude(p => p.Origin).FirstOrDefaultAsync(s => s.Id == input.SubscriptionId, ct);
        if (sub is null) return Results.NotFound(new { error = "Suscripción no encontrada." });
        if (input.Ts is null || string.IsNullOrWhiteSpace(input.Ref) || string.IsNullOrWhiteSpace(input.Sig))
        {
            return Results.BadRequest(new { error = "Falta la firma." });
        }
        if (!string.Equals(sub.Ref, input.Ref, StringComparison.Ordinal) || !Signatures.TimestampFresh(input.Ts.Value, DateTimeOffset.UtcNow))
        {
            return Results.BadRequest(new { error = "La firma no es válida." });
        }
        var payload = string.Create(CultureInfo.InvariantCulture, $"{sub.Id:N}.{input.Ref}.{input.Ts}");
        var expected = Signatures.HmacHex(sub.Product.Origin.SigningSecret, payload);
        if (!Signatures.FixedEquals(expected, input.Sig)) return Results.BadRequest(new { error = "La firma no coincide." });
        return await ApplyCancel(db, transitions, sub, input.Immediate, ct);
    }

    private static async Task<IResult> ChangeCard(Guid id, ChangeCardInput input, AppDb db, WompiClient wompi, ChargeService charges, CancellationToken ct)
    {
        var session = await OpenSession(db, id, ct);
        if (session is null || !session.Signed || string.IsNullOrWhiteSpace(session.Ref))
        {
            return Results.BadRequest(new { error = "Hace falta una sesión firmada." });
        }
        if (string.IsNullOrWhiteSpace(input.CardToken)) return Results.BadRequest(new { error = "Falta el token." });
        var sub = await db.Subscriptions.Include(s => s.Product).FirstOrDefaultAsync(s =>
            s.OriginId == session.OriginId && s.Ref == session.Ref && s.ProductId == session.ProductId, ct);
        if (sub is null) return Results.NotFound(new { error = "No hay suscripción para esta referencia." });
        var acceptance = await wompi.AcceptanceTokenAsync(ct);
        var created = await wompi.CreateCardSourceAsync(input.CardToken, input.Email, acceptance, ct);
        var source = new PaymentSource
        {
            Id = Guid.NewGuid(),
            OriginId = session.OriginId,
            Ref = session.Ref,
            WompiSourceId = created.Id,
            Type = "CARD",
            Brand = created.Brand,
            LastFour = created.LastFour,
            Status = "AVAILABLE",
            CustomerEmail = input.Email,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PaymentSources.Add(source);
        sub.PaymentSourceId = source.Id;
        var retry = sub.Status is "past_due" or "suspended" || sub.CancelReason == "source_invalid";
        sub.CancelReason = null;
        sub.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (retry)
        {
            await charges.RenewAsync(sub.Id, ct);
        }
        return Results.Ok(new { ok = true, lastFour = created.LastFour, retry });
    }

    private static async Task<IResult> CreateOrigin(OriginInput input, HttpRequest request, AppDb db, AppSettings settings, CancellationToken ct)
    {
        if (!Admin(request, settings)) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.SigningSecret) || string.IsNullOrWhiteSpace(input.NotifySecret))
        {
            return Results.BadRequest(new { error = "Faltan datos del origen." });
        }
        var origin = new Origin
        {
            Name = input.Name.Trim(),
            SigningSecret = input.SigningSecret.Trim(),
            NotifySecret = input.NotifySecret.Trim(),
            MailFrom = string.IsNullOrWhiteSpace(input.MailFrom)
                ? "Pagos Hernan Elejalde Amin <pagos@example.com>"
                : input.MailFrom.Trim(),
            Active = true,
        };
        db.Origins.Add(origin);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { origin.Id, origin.Name });
    }

    private static async Task<IResult> CreateProduct(ProductInput input, HttpRequest request, AppDb db, AppSettings settings, CancellationToken ct)
    {
        if (!Admin(request, settings)) return Results.Unauthorized();
        var type = input.Type.Trim();
        var kind = string.IsNullOrWhiteSpace(input.NotifyKind) ? type : input.NotifyKind.Trim();
        if (!ProductRules.CanSave(type, kind, input.NotifyUrl))
        {
            return Results.BadRequest(new { error = "Un pago único no puede avisar al webhook de suscripciones, y el tipo debe coincidir." });
        }
        if (!await db.Origins.AnyAsync(o => o.Id == input.OriginId && o.Active, ct))
        {
            return Results.BadRequest(new { error = "Origen no encontrado." });
        }
        var product = new Product
        {
            OriginId = input.OriginId,
            Name = input.Name.Trim(),
            Description = input.Description?.Trim() ?? "",
            PriceCents = input.PriceCents,
            Type = type,
            NotifyUrl = string.IsNullOrWhiteSpace(input.NotifyUrl) ? null : input.NotifyUrl.Trim(),
            NotifyKind = kind,
            OriginReference = input.OriginReference,
            BillingInterval = input.BillingInterval == "year" ? "year" : "month",
            IntervalCount = input.IntervalCount <= 0 ? 1 : input.IntervalCount,
            MaxAttempts = input.MaxAttempts <= 0 ? 3 : input.MaxAttempts,
            RetryDays = string.IsNullOrWhiteSpace(input.RetryDays) ? "1,3,5" : input.RetryDays.Trim(),
            GraceDays = input.GraceDays < 0 ? 3 : input.GraceDays,
            Active = true,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { product.Id, product.Name, product.Type, product.PriceCents });
    }

    private static async Task<IResult> ListProducts(HttpRequest request, AppDb db, AppSettings settings, CancellationToken ct)
    {
        if (!Admin(request, settings)) return Results.Unauthorized();
        var rows = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(ProductView));
    }

    private static IResult DevSign(int productId, string reference, AppDb db, AppSettings settings)
    {
        if (settings.IsProduction) return Results.NotFound();
        var product = db.Products.Include(p => p.Origin).FirstOrDefault(p => p.Id == productId);
        if (product is null) return Results.NotFound(new { error = "Producto no encontrado." });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Signatures.HmacHex(product.Origin.SigningSecret, Signatures.CheckoutPayload(productId, reference, ts));
        var query = $"id={productId}&ref={Uri.EscapeDataString(reference)}&ts={ts}&sig={sig}";
        return Results.Ok(new { path = "/pagar?" + query, ts, sig });
    }

    private static async Task<IResult> ApplyCancel(AppDb db, TransitionService transitions, Subscription sub, bool immediate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        sub.CanceledAt = now;
        sub.UpdatedAt = now;
        if (immediate)
        {
            sub.Status = "canceled";
            sub.CancelAtPeriodEnd = false;
            sub.AccessEndNotified = true;
            await transitions.NotifySubscriptionAsync(sub, sub.Product, sub.Product.Origin, "inactive", "subscription.canceled", null, ct);
        }
        else
        {
            sub.CancelAtPeriodEnd = true;
            await transitions.NotifySubscriptionAsync(sub, sub.Product, sub.Product.Origin, "canceled", "subscription.canceled", null, ct);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true, cancelAtPeriodEnd = sub.CancelAtPeriodEnd, currentPeriodEnd = sub.CurrentPeriodEnd });
    }

    private static bool ValidBodySignature(HttpRequest request, string raw, string secret)
    {
        if (!request.Headers.TryGetValue("x-timestamp", out var tsHeader) ||
            !request.Headers.TryGetValue("x-signature", out var sigHeader) ||
            !long.TryParse(tsHeader, out var ts))
        {
            return false;
        }
        if (!Signatures.TimestampFresh(ts, DateTimeOffset.UtcNow, maxAgeSeconds: 300, maxFutureSeconds: 60)) return false;
        var expected = Signatures.HmacHex(secret, Signatures.SignedBody(ts, raw));
        return Signatures.FixedEquals(expected, sigHeader.ToString());
    }

    private static async Task<string> ReadBody(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var raw = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return raw;
    }

    private static async Task<CheckoutSession?> OpenSession(AppDb db, Guid id, CancellationToken ct)
    {
        var session = await db.CheckoutSessions.Include(s => s.Product).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null || session.ExpiresAt < DateTimeOffset.UtcNow) return null;
        return session;
    }

    private static object ProductView(Product product) => new
    {
        id = product.Id,
        name = product.Name,
        description = product.Description,
        priceCents = product.PriceCents,
        type = product.Type,
        billingInterval = product.BillingInterval,
        intervalCount = product.IntervalCount,
        graceDays = product.GraceDays,
        originReference = product.OriginReference,
    };

    private static bool Admin(HttpRequest request, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AdminToken)) return false;
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return Signatures.FixedEquals(settings.AdminToken, header[prefix.Length..].Trim());
    }

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

    private static string BaseUrl(HttpContext http, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PublicBaseUrl)) return settings.PublicBaseUrl.TrimEnd('/');
        return $"{http.Request.Scheme}://{http.Request.Host}";
    }
}

public sealed class CreateSessionInput
{
    public int ProductId { get; set; }
    public string? Ref { get; set; }
    public long? Ts { get; set; }
    public string? Sig { get; set; }
}

public sealed class PageCancelInput
{
    public Guid SubscriptionId { get; set; }
    public string? Ref { get; set; }
    public long? Ts { get; set; }
    public string? Sig { get; set; }
    public bool Immediate { get; set; }
}

public sealed class ChangeCardInput
{
    public string CardToken { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class OriginInput
{
    public string Name { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public string NotifySecret { get; set; } = "";
    public string? MailFrom { get; set; }
}

public sealed class ProductInput
{
    public int OriginId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long PriceCents { get; set; }
    public string Type { get; set; } = "one_time";
    public string? NotifyUrl { get; set; }
    public string? NotifyKind { get; set; }
    public string? OriginReference { get; set; }
    public string? BillingInterval { get; set; }
    public int IntervalCount { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
    public string? RetryDays { get; set; }
    public int GraceDays { get; set; } = 3;
}
