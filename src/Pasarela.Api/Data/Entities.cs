namespace Pasarela.Data;

public sealed class Origin
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public string NotifySecret { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class Product
{
    public int Id { get; set; }
    public int OriginId { get; set; }
    public Origin Origin { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long PriceCents { get; set; }
    public string Type { get; set; } = "one_time";
    public bool Active { get; set; } = true;
    public string? NotifyUrl { get; set; }
    public string NotifyKind { get; set; } = "one_time";
    public string? OriginReference { get; set; }
    public string BillingInterval { get; set; } = "month";
    public int IntervalCount { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
    public string RetryDays { get; set; } = "1,3,5";
    public int GraceDays { get; set; } = 3;
}

public sealed class CheckoutSession
{
    public Guid Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string? Ref { get; set; }
    public int OriginId { get; set; }
    public bool Signed { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Acceptance
{
    public long Id { get; set; }
    public Guid? CheckoutSessionId { get; set; }
    public Guid? PaymentId { get; set; }
    public string Kind { get; set; } = "";
    public string TextVersion { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
    public string Ip { get; set; } = "";
}

public sealed class PaymentSource
{
    public Guid Id { get; set; }
    public int OriginId { get; set; }
    public string Ref { get; set; } = "";
    public string WompiSourceId { get; set; } = "";
    public string Type { get; set; } = "CARD";
    public string? Brand { get; set; }
    public string? LastFour { get; set; }
    public string Status { get; set; } = "AVAILABLE";
    public string? CustomerEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public int OriginId { get; set; }
    public string Ref { get; set; } = "";
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid? PaymentSourceId { get; set; }
    public PaymentSource? PaymentSource { get; set; }
    public string Status { get; set; } = "incomplete";
    public long PriceCents { get; set; }
    public string Currency { get; set; } = "COP";
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset? NextBillingAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public string? CancelReason { get; set; }
    public bool AccessEndNotified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid PublicId { get; set; }
    public int OriginId { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid? SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public string? Ref { get; set; }
    public long AmountCents { get; set; }
    public string Method { get; set; } = "";
    public string? WompiTransactionId { get; set; }
    public string WompiReference { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public string? StatusDetail { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public int AttemptNumber { get; set; } = 1;
    public DateOnly? PeriodStart { get; set; }
    public string? PayerEmail { get; set; }
    public string? PayerName { get; set; }
    public string? PayerLegalId { get; set; }
    public string? PayerLegalIdType { get; set; }
    public string? PayerPhone { get; set; }
    public string? CardBrand { get; set; }
    public string? CardLastFour { get; set; }
    public string? AsyncPaymentUrl { get; set; }
    public bool FirstPeriod { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WebhookEvent
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid? PaymentId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string EventKey { get; set; } = "";
}
