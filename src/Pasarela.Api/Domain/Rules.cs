using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pasarela.Domain;

public static class Signatures
{
    public static string CheckoutPayload(int productId, string reference, long timestamp) =>
        string.Create(CultureInfo.InvariantCulture, $"{productId}.{reference}.{timestamp}");

    public static string HmacHex(string secret, string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left.Trim().ToLowerInvariant());
        var b = Encoding.UTF8.GetBytes(right.Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static bool TimestampFresh(long unixSeconds, DateTimeOffset now, int maxAgeSeconds = 900, int maxFutureSeconds = 60)
    {
        DateTimeOffset stamp;
        try
        {
            stamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (stamp > now.AddSeconds(maxFutureSeconds)) return false;
        if (stamp < now.AddSeconds(-maxAgeSeconds)) return false;
        return true;
    }

    public static string Integrity(string reference, long amountCents, string secret) =>
        Sha256Hex(string.Create(CultureInfo.InvariantCulture, $"{reference}{amountCents}COP{secret}"));

    public static string SignedBody(long timestamp, string rawBody) =>
        string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{rawBody}");
}

public static class PaymentStates
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
    public const string Error = "ERROR";
    public const string Voided = "VOIDED";

    public static bool CanMove(string from, string to)
    {
        if (from == Pending && to is Approved or Declined or Error or Voided) return true;
        if (from == Approved && to == Voided) return true;
        return false;
    }

    public static string Normalize(string? status) => (status ?? "").Trim().ToUpperInvariant() switch
    {
        Approved => Approved,
        Declined => Declined,
        Error => Error,
        Voided => Voided,
        _ => Pending,
    };

    public static bool SendsEmail(string status) =>
        status is Approved or Declined or Error or Voided;
}

public static class ProductRules
{
    public static bool IsSubscriptionWebhook(string? notifyUrl)
    {
        if (string.IsNullOrWhiteSpace(notifyUrl)) return false;
        var trimmed = notifyUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.TrimEnd('/').EndsWith("/api/subscriptions/webhook", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.TrimEnd('/').EndsWith("/api/subscriptions/webhook", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanSave(string type, string notifyKind, string? notifyUrl)
    {
        if (type is not ("one_time" or "subscription")) return false;
        if (notifyKind != type) return false;
        if (type == "one_time" && IsSubscriptionWebhook(notifyUrl)) return false;
        return true;
    }
}

public static class BillingMath
{
    public static DateTimeOffset AddInterval(DateTimeOffset start, string interval, int count)
    {
        var step = Math.Max(1, count);
        return interval == "year" ? start.AddYears(step) : start.AddMonths(step);
    }

    public static DateTimeOffset NextRetry(string retryDays, int attemptCount, DateTimeOffset now)
    {
        var days = retryDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToArray();
        if (days.Length == 0) return now.AddDays(1);
        var index = Math.Clamp(attemptCount - 1, 0, days.Length - 1);
        return now.AddDays(days[index]);
    }
}

public static class WompiChecksum
{
    public static string Build(JsonElement data, IReadOnlyList<string> properties, long timestamp, string eventsSecret)
    {
        var raw = new StringBuilder();
        foreach (var property in properties)
        {
            raw.Append(Read(data, property));
        }

        raw.Append(timestamp.ToString(CultureInfo.InvariantCulture));
        raw.Append(eventsSecret);
        return Signatures.Sha256Hex(raw.ToString());
    }

    public static string Read(JsonElement data, string path)
    {
        var current = data;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return "";
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? "",
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => current.GetRawText(),
        };
    }
}

public static class MailMask
{
    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return "";
        var parts = email.Split('@', 2);
        var name = parts[0];
        var visible = name.Length == 0 ? "" : name.Length <= 2 ? name[..1] : name[..2];
        return visible + "***@" + parts[1];
    }
}

public static class LegalCopy
{
    public const string PrivacyVersion = "2026-10-28-todo-legal";
    public const string RecurringVersion = "2026-10-28-todo-legal";

    public const string PrivacyText =
        "TODO legal. Autorizo el tratamiento de mis datos personales (correo, teléfono y documento) para procesar este pago, según la Ley 1581 de 2012. Este texto está pendiente de revisión del abogado.";

    public static string RecurringText(string productName, long priceCents, string interval, int intervalCount)
    {
        var money = (priceCents / 100m).ToString("N0", new CultureInfo("es-CO"));
        var every = interval == "year"
            ? (intervalCount == 1 ? "cada año" : $"cada {intervalCount} años")
            : (intervalCount == 1 ? "cada mes" : $"cada {intervalCount} meses");
        return
            $"TODO legal. Vas a suscribirte a {productName} por ${money} COP {every}. " +
            "La renovación es automática: al terminar cada periodo se cobra de nuevo la misma fuente de pago. " +
            "Puedes cancelar antes del siguiente cobro y el acceso sigue hasta el fin del periodo ya pagado. " +
            "Este texto está pendiente de revisión del abogado.";
    }
}
