using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pasarela.Services;

public sealed class WompiOptions
{
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string IntegritySecret { get; set; } = "";
    public string EventsSecret { get; set; } = "";

    public string BaseUrl =>
        PrivateKey.StartsWith("prv_test_", StringComparison.Ordinal) ||
        PublicKey.StartsWith("pub_test_", StringComparison.Ordinal)
            ? "https://sandbox.wompi.co/v1"
            : "https://production.wompi.co/v1";
}

public sealed record WompiCardSource(string Id, string? Brand, string? LastFour);

public sealed record WompiTransaction(
    string Id,
    string Status,
    string? AsyncPaymentUrl,
    string? StatusMessage);

public sealed class WompiException(string message) : Exception(message)
{
    public bool SourceInvalid { get; init; }
}

public sealed class WompiClient(HttpClient http, WompiOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> AcceptanceTokenAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"merchants/{Uri.EscapeDataString(options.PublicKey)}", null, options.PublicKey, ct);
        var token = doc.RootElement.GetProperty("data").GetProperty("presigned_acceptance").GetProperty("acceptance_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new WompiException("Wompi no devolvió acceptance_token.");
        return token;
    }

    public async Task<IReadOnlyList<WompiBank>> BanksAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "pse/financial_institutions", null, options.PublicKey, ct);
        var list = new List<WompiBank>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var code = item.TryGetProperty("financial_institution_code", out var c) ? c.GetString() : null;
            var name = item.TryGetProperty("financial_institution_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(name))
            {
                list.Add(new WompiBank(code, name));
            }
        }
        return list;
    }

    public async Task<WompiCardSource> CreateCardSourceAsync(string token, string email, string acceptanceToken, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "CARD",
            token,
            customer_email = email,
            acceptance_token = acceptanceToken,
        }, Json);
        using var doc = await SendAsync(HttpMethod.Post, "payment_sources", body, options.PrivateKey, ct);
        var data = doc.RootElement.GetProperty("data");
        var id = IdOf(data.GetProperty("id"));
        string? brand = null;
        string? last4 = null;
        if (data.TryGetProperty("public_data", out var pub) && pub.ValueKind == JsonValueKind.Object)
        {
            brand = pub.TryGetProperty("brand", out var b) ? b.GetString() : null;
            last4 = pub.TryGetProperty("last_four", out var l) ? l.GetString() : null;
        }
        return new WompiCardSource(id, brand, last4);
    }

    public async Task<WompiTransaction> CreateTransactionAsync(object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload, Json);
        using var doc = await SendAsync(HttpMethod.Post, "transactions", body, options.PrivateKey, ct);
        return ReadTransaction(doc.RootElement.GetProperty("data"));
    }

    public async Task<WompiTransaction?> GetAsync(string transactionId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"transactions/{Uri.EscapeDataString(transactionId)}", null, options.PrivateKey, ct);
        return ReadTransaction(doc.RootElement.GetProperty("data"));
    }

    public async Task<WompiTransaction?> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"transactions?reference={Uri.EscapeDataString(reference)}", null, options.PrivateKey, ct);
        var data = doc.RootElement.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() == 0) return null;
            return ReadTransaction(data[0]);
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _))
        {
            return ReadTransaction(data);
        }
        return null;
    }

    private static WompiTransaction ReadTransaction(JsonElement data)
    {
        var id = IdOf(data.GetProperty("id"));
        var status = data.GetProperty("status").GetString() ?? "PENDING";
        string? url = null;
        string? message = data.TryGetProperty("status_message", out var msg) ? msg.GetString() : null;
        if (data.TryGetProperty("payment_method", out var method) &&
            method.TryGetProperty("extra", out var extra) &&
            extra.ValueKind == JsonValueKind.Object &&
            extra.TryGetProperty("async_payment_url", out var asyncUrl))
        {
            url = asyncUrl.GetString();
        }
        return new WompiTransaction(id, status, url, message);
    }

    private static string IdOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, options.BaseUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var invalid = raw.Contains("payment_source", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("fuente de pago", StringComparison.OrdinalIgnoreCase);
            throw new WompiException($"Wompi respondió {(int)response.StatusCode}: {Trim(raw)}")
            {
                SourceInvalid = invalid,
            };
        }

        return JsonDocument.Parse(raw);
    }

    private static string Trim(string raw) => raw.Length <= 400 ? raw : raw[..400];
}

public sealed record WompiBank(string Code, string Name);

public static class Money
{
    public static string Cop(long cents) =>
        (cents / 100m).ToString("C0", new CultureInfo("es-CO"));
}
