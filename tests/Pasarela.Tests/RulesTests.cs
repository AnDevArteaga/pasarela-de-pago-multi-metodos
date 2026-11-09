using System.Text.Json;
using Pasarela.Domain;

namespace Pasarela.Tests;

public class SignatureTests
{
    [Fact]
    public void Checkout_payload_separates_fields_with_dots()
    {
        Assert.Equal("12.user-1.1700000000", Signatures.CheckoutPayload(12, "user-1", 1700000000));
    }

    [Fact]
    public void A_missing_dot_does_not_match_the_signed_payload()
    {
        const string secret = "origin-secret";
        var dotted = Signatures.HmacHex(secret, "1.ab.10");
        var concatenated = Signatures.HmacHex(secret, "1ab10");
        Assert.False(Signatures.FixedEquals(dotted, concatenated));
    }

    [Fact]
    public void A_tampered_reference_fails_the_compare()
    {
        const string secret = "origin-secret";
        var original = Signatures.HmacHex(secret, Signatures.CheckoutPayload(4, "ref-a", 20));
        var tampered = Signatures.HmacHex(secret, Signatures.CheckoutPayload(4, "ref-b", 20));
        Assert.False(Signatures.FixedEquals(original, tampered));
    }

    [Fact]
    public void An_expired_timestamp_is_rejected()
    {
        var now = DateTimeOffset.Parse("2026-10-28T15:00:00Z");
        var ts = now.AddMinutes(-16).ToUnixTimeSeconds();
        Assert.False(Signatures.TimestampFresh(ts, now));
    }

    [Fact]
    public void A_timestamp_more_than_a_minute_ahead_is_rejected()
    {
        var now = DateTimeOffset.Parse("2026-10-28T15:00:00Z");
        var ts = now.AddMinutes(2).ToUnixTimeSeconds();
        Assert.False(Signatures.TimestampFresh(ts, now));
    }

    [Fact]
    public void A_fresh_timestamp_is_accepted()
    {
        var now = DateTimeOffset.Parse("2026-10-28T15:00:00Z");
        Assert.True(Signatures.TimestampFresh(now.ToUnixTimeSeconds(), now));
    }

    [Fact]
    public void Integrity_concatenates_reference_amount_currency_and_secret()
    {
        var expected = Signatures.Sha256Hex("pay-1" + "500000" + "COP" + "integrity");
        Assert.Equal(expected, Signatures.Integrity("pay-1", 500000, "integrity"));
    }
}

public class StateTests
{
    [Theory]
    [InlineData("PENDING", "APPROVED", true)]
    [InlineData("PENDING", "DECLINED", true)]
    [InlineData("PENDING", "ERROR", true)]
    [InlineData("PENDING", "VOIDED", true)]
    [InlineData("APPROVED", "VOIDED", true)]
    [InlineData("APPROVED", "DECLINED", false)]
    [InlineData("DECLINED", "APPROVED", false)]
    [InlineData("ERROR", "APPROVED", false)]
    public void Only_pending_and_void_from_approved_are_valid(string from, string to, bool allowed)
    {
        Assert.Equal(allowed, PaymentStates.CanMove(from, to));
    }
}

public class ChecksumTests
{
    [Fact]
    public void Event_checksum_walks_properties_then_timestamp_then_secret()
    {
        using var doc = JsonDocument.Parse("""
            {"transaction":{"id":"tx-9","status":"APPROVED","amount_in_cents":1500}}
            """);
        var properties = new[] { "transaction.id", "transaction.status", "transaction.amount_in_cents" };
        var built = WompiChecksum.Build(doc.RootElement, properties, 99, "events");
        Assert.Equal(Signatures.Sha256Hex("tx-9APPROVED150099events"), built);
    }
}

public class ProductRuleTests
{
    [Fact]
    public void One_time_cannot_point_at_the_subscription_webhook()
    {
        Assert.False(ProductRules.CanSave("one_time", "one_time", "https://maquina.example/api/subscriptions/webhook"));
        Assert.True(ProductRules.CanSave("subscription", "subscription", "https://maquina.example/api/subscriptions/webhook"));
        Assert.True(ProductRules.CanSave("one_time", "one_time", "https://maquina.example/api/purchases/webhook"));
        Assert.False(ProductRules.CanSave("one_time", "subscription", "https://maquina.example/api/purchases/webhook"));
    }
}

public class BillingMathTests
{
    [Fact]
    public void Retry_days_follow_the_attempt_number()
    {
        var now = DateTimeOffset.Parse("2026-10-28T15:00:00Z");
        Assert.Equal(now.AddDays(1), BillingMath.NextRetry("1,3,5", 1, now));
        Assert.Equal(now.AddDays(3), BillingMath.NextRetry("1,3,5", 2, now));
        Assert.Equal(now.AddDays(5), BillingMath.NextRetry("1,3,5", 3, now));
    }

    [Fact]
    public void A_month_interval_adds_months_from_the_period_end()
    {
        var start = DateTimeOffset.Parse("2026-01-31T12:00:00Z");
        Assert.Equal(start.AddMonths(1), BillingMath.AddInterval(start, "month", 1));
        Assert.Equal(start.AddYears(1), BillingMath.AddInterval(start, "year", 1));
    }
}

public class MailMaskTests
{
    [Fact]
    public void Email_keeps_two_characters_and_the_domain()
    {
        Assert.Equal("ma***@gmail.com", MailMask.Mask("maria@gmail.com"));
        Assert.Equal("", MailMask.Mask("sin-arroba"));
    }
}
