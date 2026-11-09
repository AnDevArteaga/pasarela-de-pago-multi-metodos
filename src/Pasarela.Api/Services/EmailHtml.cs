using System.Globalization;
using System.Net;
using System.Text;
using Pasarela.Data;
using Pasarela.Domain;

namespace Pasarela.Services;

public static class EmailHtml
{
    public static string Subject(string status, string product) => status switch
    {
        PaymentStates.Approved => $"Compra aprobada — {product}",
        PaymentStates.Voided => $"Compra anulada — {product}",
        _ => $"Compra rechazada — {product}",
    };

    public static (string Title, string Lead) Copy(string status) => status switch
    {
        PaymentStates.Approved => ("Compra aprobada", "El pago quedó aprobado."),
        PaymentStates.Voided => ("Compra anulada", "El pago aprobado fue anulado."),
        PaymentStates.Error => ("Compra rechazada", "No se pudo completar el pago."),
        _ => ("Compra rechazada", "El pago no fue aprobado."),
    };

    public static string Render(Payment payment, Product product)
    {
        var (title, lead) = Copy(payment.Status);
        var type = product.Type == "subscription" ? "Suscripción" : "Pago único";
        var rows = new (string Label, string Value)[]
        {
            ("Producto", Web(product.Name)),
            ("Tipo", type),
            ("Monto", Web(Money.Cop(payment.AmountCents))),
            ("Método", Web(MethodLabel(payment.Method))),
            ("Referencia", Web(payment.WompiReference)),
            ("Id Wompi", Web(payment.WompiTransactionId ?? "—")),
            ("Fecha", Web(payment.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))),
            ("Estado", Web(payment.Status)),
        };
        if (!string.IsNullOrWhiteSpace(payment.CardLastFour))
        {
            rows = rows.Append(("Tarjeta", Web($"{payment.CardBrand} ···· {payment.CardLastFour}"))).ToArray();
        }

        var body = new StringBuilder();
        foreach (var row in rows)
        {
            body.Append($"""
                <tr>
                  <td style="padding:10px 0;border-bottom:1px solid #e6e6e6;color:#666;font-size:13px;width:34%;">{row.Label}</td>
                  <td style="padding:10px 0;border-bottom:1px solid #e6e6e6;color:#111;font-size:15px;">{row.Value}</td>
                </tr>
                """);
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="es">
            <body style="margin:0;background:#f4f4f4;font-family:Georgia, 'Times New Roman', serif;color:#111;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f4f4;padding:32px 12px;">
                <tr><td align="center">
                  <table role="presentation" width="100%" style="max-width:520px;background:#fff;border:1px solid #111;">
                    <tr><td style="padding:28px 28px 8px;">
                      <p style="margin:0;letter-spacing:.14em;text-transform:uppercase;font-size:11px;font-family:Arial,sans-serif;">Pasarela</p>
                      <h1 style="margin:12px 0 8px;font-size:28px;font-weight:500;">{{Web(title)}}</h1>
                      <p style="margin:0 0 18px;font-family:Arial,sans-serif;font-size:15px;line-height:1.5;">{{Web(lead)}}</p>
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="0">{{body}}</table>
                      <p style="margin:22px 0 0;font-family:Arial,sans-serif;font-size:12px;color:#666;">Pagos Hernan Elejalde Amin</p>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    public static string CardUpdate(string product) =>
        $"""
        <!DOCTYPE html>
        <html lang="es"><body style="margin:0;background:#f4f4f4;font-family:Arial,sans-serif;color:#111;">
        <table width="100%" style="background:#f4f4f4;padding:32px 12px;"><tr><td align="center">
        <table width="100%" style="max-width:520px;background:#fff;border:1px solid #111;padding:28px;">
        <h1 style="font-family:Georgia,serif;font-weight:500;font-size:26px;">Actualiza tu tarjeta</h1>
        <p>No pudimos cobrar {Web(product)} porque la fuente de pago ya no es válida. Entra de nuevo al pago y registra otra tarjeta.</p>
        <p style="color:#666;font-size:12px;">Pagos Hernan Elejalde Amin</p>
        </table></td></tr></table></body></html>
        """;

    private static string MethodLabel(string method) => method switch
    {
        "CARD" => "Tarjeta",
        "PSE" => "PSE",
        "NEQUI" => "Nequi",
        "BANCOLOMBIA_TRANSFER" => "Botón Bancolombia",
        _ => method,
    };

    private static string Web(string value) => WebUtility.HtmlEncode(value);
}
