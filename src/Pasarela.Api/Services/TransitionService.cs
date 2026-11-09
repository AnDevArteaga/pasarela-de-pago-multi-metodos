using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pasarela.Data;
using Pasarela.Domain;

namespace Pasarela.Services;

public sealed class TransitionService(AppDb db, ILogger<TransitionService> log)
{
    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<bool> TransitionAsync(Guid paymentId, string requested, string source, string? detail, CancellationToken ct)
    {
        var next = PaymentStates.Normalize(requested);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payments WHERE id = {paymentId} FOR UPDATE", ct);

        var payment = await db.Payments
            .Include(p => p.Product)
            .ThenInclude(p => p.Origin)
            .Include(p => p.Subscription)
            .ThenInclude(s => s!.Product)
            .ThenInclude(p => p.Origin)
            .FirstAsync(p => p.Id == paymentId, ct);

        if (payment.Status == next)
        {
            log.LogInformation("Pago {PaymentId} ya está en {Status}. Origen {Source}", payment.Id, next, source);
            await tx.CommitAsync(ct);
            return false;
        }

        if (!PaymentStates.CanMove(payment.Status, next))
        {
            log.LogInformation(
                "Transición ignorada {PaymentId} {From} -> {To}. Origen {Source}",
                payment.Id, payment.Status, next, source);
            await tx.CommitAsync(ct);
            return false;
        }

        var from = payment.Status;
        var now = DateTimeOffset.UtcNow;
        payment.Status = next;
        payment.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(detail)) payment.StatusDetail = detail;

        await ApplyAsync(payment, next, detail, now, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Pago {PaymentId} {From} -> {To} por {Source}", payment.Id, from, next, source);
        return true;
    }

    private async Task ApplyAsync(Payment payment, string next, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        if (PaymentStates.SendsEmail(next) && !string.IsNullOrWhiteSpace(payment.PayerEmail))
        {
            await EnqueueAsync(payment, "send_email", $"email:{next}:{payment.Id}", new
            {
                payment.PublicId,
                payment.Status,
            }, null, now, ct);
        }

        if (payment.SubscriptionId is null || payment.Subscription is null)
        {
            if (next is PaymentStates.Approved or PaymentStates.Voided)
            {
                await EnqueueNotifyAsync(payment, payment.Product, payment.Product.Origin, next == PaymentStates.Approved ? "active" : "inactive",
                    next == PaymentStates.Approved ? "payment.approved" : "payment.voided", null, now, ct);
            }
            return;
        }

        var sub = payment.Subscription;
        var product = sub.Product;
        if (next == PaymentStates.Approved)
        {
            var start = payment.FirstPeriod || sub.CurrentPeriodEnd is null
                ? now
                : sub.CurrentPeriodEnd.Value;
            var end = BillingMath.AddInterval(start, product.BillingInterval, product.IntervalCount);
            sub.CurrentPeriodStart = start;
            sub.CurrentPeriodEnd = end;
            sub.NextBillingAt = end;
            sub.AttemptCount = 0;
            sub.NextRetryAt = null;
            sub.Status = "active";
            sub.CancelReason = null;
            sub.AccessEndNotified = false;
            sub.UpdatedAt = now;
            var evt = payment.FirstPeriod ? "payment.approved" : "subscription.renewed";
            await EnqueueNotifyAsync(payment, product, product.Origin, "active", evt, end, now, ct);
            return;
        }

        if (next is PaymentStates.Declined or PaymentStates.Error)
        {
            if (payment.FirstPeriod)
            {
                sub.Status = "incomplete";
                sub.UpdatedAt = now;
                return;
            }

            if (string.Equals(detail, "source_invalid", StringComparison.Ordinal))
            {
                sub.Status = "past_due";
                sub.CancelReason = "source_invalid";
                sub.NextRetryAt = null;
                sub.UpdatedAt = now;
                await EnqueueAsync(payment, "send_email", $"email:source_invalid:{payment.Id}", new
                {
                    kind = "source_invalid",
                    payment.PublicId,
                }, sub.Id, now, ct);
                return;
            }

            sub.AttemptCount += 1;
            sub.UpdatedAt = now;
            if (sub.AttemptCount >= product.MaxAttempts)
            {
                sub.Status = "suspended";
                sub.NextRetryAt = null;
                await EnqueueNotifyAsync(payment, product, product.Origin, "inactive", "subscription.suspended", sub.CurrentPeriodEnd, now, ct);
            }
            else
            {
                sub.Status = "past_due";
                sub.NextRetryAt = BillingMath.NextRetry(product.RetryDays, sub.AttemptCount, now);
            }
            return;
        }

        if (next == PaymentStates.Voided)
        {
            sub.Status = "suspended";
            sub.UpdatedAt = now;
            sub.AccessEndNotified = true;
            await EnqueueNotifyAsync(payment, product, product.Origin, "inactive", "payment.voided", sub.CurrentPeriodEnd, now, ct);
        }
    }

    public async Task NotifySubscriptionAsync(Subscription sub, Product product, Origin origin, string status, string evt, Guid? paymentId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var key = $"{evt}:{sub.Id}:{status}";
        if (await db.Outbox.AnyAsync(o => o.Type == "notify_origin" && o.EventKey == key, ct)) return;
        if (string.IsNullOrWhiteSpace(product.NotifyUrl)) return;
        if (product.Type == "one_time" && ProductRules.IsSubscriptionWebhook(product.NotifyUrl)) return;

        var payload = new
        {
            UserId = sub.Ref,
            Status = status,
            ExternalReference = product.OriginReference,
            ExternalSubscriptionId = sub.Id.ToString(),
            CurrentPeriodEnd = sub.CurrentPeriodEnd,
            PaymentId = paymentId,
            PaymentStatus = status,
            AmountInCents = sub.PriceCents,
            Event = evt,
            EventId = id,
            OccurredAt = now,
        };
        db.Outbox.Add(new OutboxMessage
        {
            Id = id,
            Type = "notify_origin",
            Payload = JsonSerializer.Serialize(payload, Snake),
            PaymentId = paymentId,
            SubscriptionId = sub.Id,
            Status = "pending",
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            EventKey = key,
        });
    }

    private async Task EnqueueNotifyAsync(
        Payment payment,
        Product product,
        Origin origin,
        string accessStatus,
        string evt,
        DateTimeOffset? periodEnd,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(product.NotifyUrl)) return;
        if (product.NotifyKind == "one_time" && ProductRules.IsSubscriptionWebhook(product.NotifyUrl))
        {
            log.LogWarning("Producto {ProductId} de pago único apunta al webhook de suscripciones. No se avisa.", product.Id);
            return;
        }

        var id = Guid.NewGuid();
        var key = $"{evt}:{payment.Id}";
        if (await db.Outbox.AnyAsync(o => o.Type == "notify_origin" && o.EventKey == key, ct)) return;
        var payload = new
        {
            UserId = payment.Ref ?? "",
            Status = accessStatus,
            ExternalReference = product.OriginReference,
            ExternalSubscriptionId = payment.SubscriptionId?.ToString(),
            CurrentPeriodEnd = product.Type == "one_time" ? null : periodEnd,
            PaymentId = payment.Id,
            PaymentStatus = payment.Status,
            AmountInCents = payment.AmountCents,
            Event = evt,
            EventId = id,
            OccurredAt = now,
        };
        db.Outbox.Add(new OutboxMessage
        {
            Id = id,
            Type = "notify_origin",
            Payload = JsonSerializer.Serialize(payload, Snake),
            PaymentId = payment.Id,
            SubscriptionId = payment.SubscriptionId,
            Status = "pending",
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            EventKey = key,
        });
        _ = origin;
    }

    private async Task EnqueueAsync(Payment payment, string type, string key, object payload, Guid? subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Outbox.AnyAsync(o => o.Type == type && o.EventKey == key, ct)) return;
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload, Snake),
            PaymentId = payment.Id,
            SubscriptionId = subscriptionId ?? payment.SubscriptionId,
            Status = "pending",
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            EventKey = key,
        });
    }
}
