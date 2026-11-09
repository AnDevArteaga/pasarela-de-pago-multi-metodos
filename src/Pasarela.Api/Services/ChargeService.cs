using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pasarela.Data;
using Pasarela.Domain;

namespace Pasarela.Services;

public sealed class AppSettings
{
    public string EnvironmentName { get; set; } = "test";
    public string PublicBaseUrl { get; set; } = "";
    public string AdminToken { get; set; } = "";
    public string ResendApiKey { get; set; } = "";
    public int RequestsPerMinute { get; set; } = 10;
    public int FailedChargesPerHour { get; set; } = 5;
    public bool IsProduction => EnvironmentName.Equals("production", StringComparison.OrdinalIgnoreCase);
}

public sealed class PayerInput
{
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string LegalId { get; set; } = "";
    public string LegalIdType { get; set; } = "CC";
}

public sealed class ChargeInput
{
    public string IdempotencyKey { get; set; } = "";
    public long AmountCents { get; set; }
    public string Method { get; set; } = "";
    public bool UseSavedSource { get; set; }
    public Guid? SavedSourceId { get; set; }
    public string? CardToken { get; set; }
    public int Installments { get; set; } = 1;
    public PayerInput Payer { get; set; } = new();
    public string? FinancialInstitutionCode { get; set; }
    public int UserType { get; set; }
    public string? NequiPhone { get; set; }
    public bool AcceptPrivacy { get; set; }
    public bool AcceptRecurring { get; set; }
}

public sealed class RateLimiter(AppSettings settings)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _hits = new();

    public bool AllowRequest(string ip) => Allow(ip, failedCharge: false, record: true);

    public bool TooManyFailures(string ip) => !Allow(ip, failedCharge: true, record: false);

    public void RecordFailure(string ip) => Allow(ip, failedCharge: true, record: true);

    private bool Allow(string ip, bool failedCharge, bool record)
    {
        if (!settings.IsProduction) return true;
        var now = DateTimeOffset.UtcNow;
        var key = (failedCharge ? "c:" : "r:") + ip;
        var window = failedCharge ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);
        var limit = failedCharge ? settings.FailedChargesPerHour : settings.RequestsPerMinute;
        var list = _hits.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t < now - window);
            if (list.Count >= limit) return false;
            if (record) list.Add(now);
            return true;
        }
    }
}

public sealed class ChargeService(
    AppDb db,
    WompiClient wompi,
    WompiOptions wompiOptions,
    TransitionService transitions,
    AppSettings settings,
    RateLimiter rates,
    ILogger<ChargeService> log)
{
    public async Task<(int Status, object Body)> ChargeAsync(Guid sessionId, ChargeInput input, string ip, string publicBase, CancellationToken ct)
    {
        var session = await db.CheckoutSessions.Include(s => s.Product).ThenInclude(p => p.Origin)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return (404, new { error = "La sesión no existe." });
        if (session.ExpiresAt < DateTimeOffset.UtcNow) return (410, new { error = "La sesión expiró." });

        var product = session.Product;
        if (!product.Active) return (404, new { error = "El producto no está activo." });
        if (input.AmountCents != product.PriceCents)
        {
            return (409, new { error = "El precio cambió.", priceCents = product.PriceCents });
        }

        if (!session.Signed && (!rates.AllowRequest(ip) || rates.TooManyFailures(ip)))
        {
            return (429, new { error = "Demasiadas solicitudes. Espera un momento." });
        }

        var existing = await db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == input.IdempotencyKey, ct);
        if (existing != null) return (200, View(existing));

        var method = input.Method.Trim().ToUpperInvariant();
        if (product.Type == "subscription" && method != "CARD")
        {
            return (400, new { error = "La suscripción se paga con tarjeta." });
        }
        if (method is not ("CARD" or "PSE" or "NEQUI" or "BANCOLOMBIA_TRANSFER"))
        {
            return (400, new { error = "Método de pago no válido." });
        }
        if (!input.AcceptPrivacy) return (400, new { error = "Falta aceptar el tratamiento de datos." });
        if (product.Type == "subscription" && !input.AcceptRecurring)
        {
            return (400, new { error = "Falta aceptar el cobro recurrente." });
        }
        if (string.IsNullOrWhiteSpace(input.Payer.Email) || !input.Payer.Email.Contains('@'))
        {
            return (400, new { error = "El correo no es válido." });
        }

        PaymentSource? saved = null;
        if (method == "CARD" && input.UseSavedSource)
        {
            if (!session.Signed || string.IsNullOrWhiteSpace(session.Ref))
            {
                return (400, new { error = "Para reutilizar la tarjeta hace falta una referencia firmada." });
            }
            saved = await db.PaymentSources.FirstOrDefaultAsync(s =>
                s.Id == input.SavedSourceId && s.OriginId == session.OriginId && s.Ref == session.Ref && s.Status == "AVAILABLE", ct);
            if (saved is null) return (404, new { error = "No encontramos esa fuente de pago." });
        }
        else if (method == "CARD" && string.IsNullOrWhiteSpace(input.CardToken))
        {
            return (400, new { error = "Falta el token de la tarjeta." });
        }
        else if (method == "PSE" && string.IsNullOrWhiteSpace(input.FinancialInstitutionCode))
        {
            return (400, new { error = "Elige el banco." });
        }
        else if (method == "NEQUI" && !Regex.IsMatch(input.NequiPhone ?? "", "^\\d{10}$"))
        {
            return (400, new { error = "El celular Nequi debe tener 10 dígitos." });
        }

        var now = DateTimeOffset.UtcNow;
        Subscription? subscription = null;
        var firstPeriod = false;
        if (product.Type == "subscription")
        {
            subscription = await db.Subscriptions.FirstOrDefaultAsync(s =>
                s.OriginId == session.OriginId && s.Ref == session.Ref && s.ProductId == product.Id &&
                s.Status != "expired" && s.Status != "canceled", ct);
            if (subscription is null)
            {
                subscription = new Subscription
                {
                    Id = Guid.NewGuid(),
                    OriginId = session.OriginId,
                    Ref = session.Ref ?? "",
                    ProductId = product.Id,
                    Status = "incomplete",
                    PriceCents = product.PriceCents,
                    Currency = "COP",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Subscriptions.Add(subscription);
                firstPeriod = true;
            }
            else
            {
                firstPeriod = subscription.Status == "incomplete";
            }
        }

        string? sourceWompiId = saved?.WompiSourceId;
        string? brand = saved?.Brand;
        string? last4 = saved?.LastFour;
        if (method == "CARD" && saved is null)
        {
            try
            {
                var acceptance = await wompi.AcceptanceTokenAsync(ct);
                var created = await wompi.CreateCardSourceAsync(input.CardToken!, input.Payer.Email.Trim(), acceptance, ct);
                sourceWompiId = created.Id;
                brand = created.Brand;
                last4 = created.LastFour;
                if (session.Signed && !string.IsNullOrWhiteSpace(session.Ref))
                {
                    var row = new PaymentSource
                    {
                        Id = Guid.NewGuid(),
                        OriginId = session.OriginId,
                        Ref = session.Ref,
                        WompiSourceId = created.Id,
                        Type = "CARD",
                        Brand = created.Brand,
                        LastFour = created.LastFour,
                        Status = "AVAILABLE",
                        CustomerEmail = input.Payer.Email.Trim(),
                        CreatedAt = now,
                    };
                    db.PaymentSources.Add(row);
                    if (subscription != null) subscription.PaymentSourceId = row.Id;
                }
            }
            catch (WompiException ex)
            {
                log.LogWarning(ex, "No se pudo crear la fuente de pago");
                return (502, new { error = "Wompi no aceptó la tarjeta." });
            }
        }
        else if (subscription != null && saved != null)
        {
            subscription.PaymentSourceId = saved.Id;
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid(),
            OriginId = session.OriginId,
            ProductId = product.Id,
            SubscriptionId = subscription?.Id,
            Ref = session.Ref,
            AmountCents = product.PriceCents,
            Method = method,
            WompiReference = "pending",
            Status = PaymentStates.Pending,
            IdempotencyKey = input.IdempotencyKey.Trim(),
            AttemptNumber = subscription == null ? 1 : Math.Max(1, subscription.AttemptCount + (firstPeriod ? 1 : subscription.AttemptCount == 0 ? 1 : subscription.AttemptCount + 1)),
            PeriodStart = subscription == null ? null : DateOnly.FromDateTime((subscription.CurrentPeriodEnd ?? now).UtcDateTime),
            PayerEmail = input.Payer.Email.Trim(),
            PayerName = input.Payer.FullName.Trim(),
            PayerPhone = input.Payer.Phone.Trim(),
            PayerLegalId = input.Payer.LegalId.Trim(),
            PayerLegalIdType = string.IsNullOrWhiteSpace(input.Payer.LegalIdType) ? "CC" : input.Payer.LegalIdType.Trim(),
            CardBrand = brand,
            CardLastFour = last4,
            FirstPeriod = firstPeriod,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (subscription != null && firstPeriod)
        {
            payment.AttemptNumber = 1;
            payment.PeriodStart = DateOnly.FromDateTime(now.UtcDateTime);
        }
        else if (subscription != null)
        {
            payment.AttemptNumber = subscription.AttemptCount + 1;
            payment.PeriodStart = DateOnly.FromDateTime((subscription.CurrentPeriodEnd ?? now).UtcDateTime);
        }

        payment.WompiReference = subscription == null
            ? $"pay-{payment.Id:N}"
            : $"sub{subscription.Id:N}-{payment.PeriodStart:yyyyMMdd}-a{payment.AttemptNumber}";

        db.Payments.Add(payment);
        db.Acceptances.Add(new Acceptance
        {
            CheckoutSessionId = session.Id,
            PaymentId = payment.Id,
            Kind = "privacy",
            TextVersion = LegalCopy.PrivacyVersion,
            AcceptedAt = now,
            Ip = ip,
        });
        if (product.Type == "subscription")
        {
            db.Acceptances.Add(new Acceptance
            {
                CheckoutSessionId = session.Id,
                PaymentId = payment.Id,
                Kind = "recurring",
                TextVersion = LegalCopy.RecurringVersion,
                AcceptedAt = now,
                Ip = ip,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            var again = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == payment.IdempotencyKey, ct);
            if (again != null) return (200, View(again));
            throw;
        }

        var submitted = await SubmitAsync(payment, product, input, sourceWompiId, publicBase, ct);
        if (!submitted.Ok)
        {
            if (!session.Signed) rates.RecordFailure(ip);
            await transitions.TransitionAsync(payment.Id, PaymentStates.Error, "charge", submitted.SourceInvalid ? "source_invalid" : submitted.Error, ct);
            return (502, new { error = submitted.Error ?? "No se pudo crear la transacción.", publicId = payment.PublicId });
        }

        payment.WompiTransactionId = submitted.Transaction!.Id;
        payment.AsyncPaymentUrl = submitted.Transaction.AsyncPaymentUrl;
        payment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var wompiStatus = PaymentStates.Normalize(submitted.Transaction.Status);
        if (wompiStatus != PaymentStates.Pending)
        {
            await transitions.TransitionAsync(payment.Id, wompiStatus, "charge", submitted.Transaction.StatusMessage, ct);
            await db.Entry(payment).ReloadAsync(ct);
        }

        return (200, View(payment));
    }

    public async Task RenewAsync(Guid subscriptionId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.Include(s => s.PaymentSource).Include(s => s.Product)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (sub?.PaymentSourceId is null) return;
        var source = await db.PaymentSources.FirstAsync(s => s.Id == sub.PaymentSourceId, ct);
        var now = DateTimeOffset.UtcNow;
        var attempt = sub.AttemptCount + 1;
        var period = DateOnly.FromDateTime((sub.CurrentPeriodEnd ?? now).UtcDateTime);
        var key = $"renew:{sub.Id:N}:{period:yyyyMMdd}:{attempt}";
        if (await db.Payments.AnyAsync(p => p.IdempotencyKey == key, ct)) return;
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
            PayerEmail = source.CustomerEmail,
            CardBrand = source.Brand,
            CardLastFour = source.LastFour,
            FirstPeriod = false,
            CreatedAt = now,
            UpdatedAt = now,
            WompiReference = $"sub{sub.Id:N}-{period:yyyyMMdd}-a{attempt}",
        };
        db.Payments.Add(payment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return;
        }
        await SubmitExistingAsync(payment, ct);
    }

    public async Task SubmitExistingAsync(Payment payment, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Origin).FirstAsync(p => p.Id == payment.ProductId, ct);
        var source = payment.SubscriptionId is null
            ? null
            : await db.Subscriptions.Where(s => s.Id == payment.SubscriptionId).Select(s => s.PaymentSource).FirstOrDefaultAsync(ct);
        var input = new ChargeInput
        {
            Method = payment.Method,
            Installments = 1,
            Payer = new PayerInput
            {
                Email = payment.PayerEmail ?? "",
                FullName = payment.PayerName ?? "",
                Phone = payment.PayerPhone ?? "",
                LegalId = payment.PayerLegalId ?? "",
                LegalIdType = payment.PayerLegalIdType ?? "CC",
            },
        };
        var result = await SubmitAsync(payment, product, input, source?.WompiSourceId, settings.PublicBaseUrl, ct);
        if (!result.Ok || result.Transaction is null)
        {
            await transitions.TransitionAsync(payment.Id, PaymentStates.Error, "reconcile", result.SourceInvalid ? "source_invalid" : result.Error, ct);
            return;
        }
        payment.WompiTransactionId = result.Transaction.Id;
        payment.AsyncPaymentUrl = result.Transaction.AsyncPaymentUrl;
        payment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var status = PaymentStates.Normalize(result.Transaction.Status);
        if (status != PaymentStates.Pending)
        {
            await transitions.TransitionAsync(payment.Id, status, "reconcile", result.Transaction.StatusMessage, ct);
        }
    }

    private async Task<SubmitResult> SubmitAsync(Payment payment, Product product, ChargeInput input, string? sourceId, string publicBase, CancellationToken ct)
    {
        try
        {
            var acceptance = await wompi.AcceptanceTokenAsync(ct);
            var description = Sanitize(product.Name);
            var redirect = publicBase.TrimEnd('/') + "/api/wompi/return";
            object method = payment.Method switch
            {
                "CARD" => new { type = "CARD", installments = Math.Clamp(input.Installments, 1, 36) },
                "PSE" => new
                {
                    type = "PSE",
                    user_type = input.UserType == 1 ? 1 : 0,
                    user_legal_id_type = string.IsNullOrWhiteSpace(input.Payer.LegalIdType) ? "CC" : input.Payer.LegalIdType,
                    user_legal_id = input.Payer.LegalId,
                    financial_institution_code = input.FinancialInstitutionCode,
                    payment_description = description,
                },
                "NEQUI" => new { type = "NEQUI", phone_number = input.NequiPhone },
                _ => new
                {
                    type = "BANCOLOMBIA_TRANSFER",
                    user_type = "PERSON",
                    payment_description = description,
                    ecommerce_url = redirect,
                },
            };

            var payload = new Dictionary<string, object?>
            {
                ["acceptance_token"] = acceptance,
                ["amount_in_cents"] = payment.AmountCents,
                ["currency"] = "COP",
                ["customer_email"] = payment.PayerEmail,
                ["reference"] = payment.WompiReference,
                ["signature"] = Signatures.Integrity(payment.WompiReference, payment.AmountCents, wompiOptions.IntegritySecret),
                ["redirect_url"] = redirect,
                ["customer_data"] = new
                {
                    full_name = payment.PayerName,
                    phone_number = payment.PayerPhone,
                    legal_id = payment.PayerLegalId,
                    legal_id_type = payment.PayerLegalIdType,
                },
                ["payment_method"] = method,
            };
            if (!string.IsNullOrWhiteSpace(sourceId)) payload["payment_source_id"] = sourceId;

            var tx = await wompi.CreateTransactionAsync(payload, ct);
            log.LogInformation("Cobro {PaymentId} referencia {Reference} transacción {Tx} estado {Status}", payment.Id, payment.WompiReference, tx.Id, tx.Status);
            return new SubmitResult(true, tx, null, false);
        }
        catch (WompiException ex)
        {
            log.LogWarning(ex, "Wompi rechazó el cobro {PaymentId}", payment.Id);
            return new SubmitResult(false, null, "No se pudo crear el cobro en Wompi.", ex.SourceInvalid);
        }
    }

    public static object View(Payment payment) => new
    {
        publicId = payment.PublicId,
        status = payment.Status,
        method = payment.Method,
        asyncPaymentUrl = payment.AsyncPaymentUrl,
        amountCents = payment.AmountCents,
    };

    private static string Sanitize(string name)
    {
        var clean = name.Replace("'", "", StringComparison.Ordinal).Trim();
        return clean.Length <= 64 ? clean : clean[..64];
    }

    private sealed record SubmitResult(bool Ok, WompiTransaction? Transaction, string? Error, bool SourceInvalid);
}
