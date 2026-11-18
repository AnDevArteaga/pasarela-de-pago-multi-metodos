using System.Net;
using Pasarela.Services;

namespace Pasarela.Tests;

public class WompiClientTests
{
    [Fact]
    public async Task Reads_transaction_when_payment_method_is_null()
    {
        using var http = new HttpClient(new TransactionHandler());
        var client = new WompiClient(http, new WompiOptions());

        var transaction = await client.GetAsync("tx-1", CancellationToken.None);

        Assert.Equal(new WompiTransaction("tx-1", "PENDING", null, null), transaction);
    }

    private sealed class TransactionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"id":"tx-1","status":"PENDING","payment_method":null}}"""),
            });
}
}
