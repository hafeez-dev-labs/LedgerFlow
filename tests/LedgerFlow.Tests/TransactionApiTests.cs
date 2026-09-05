using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LedgerFlow.Tests;

public class TransactionApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public TransactionApiTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateTransaction_ReturnsBalancedLedger()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = "customer-001",
                toAccount = "merchant-001",
                amount = 100.50m,
                currency = "USD"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(transaction);
        Assert.Equal(2, transaction!.LedgerEntries.Count);
        Assert.Contains(transaction.LedgerEntries, entry => entry.Type == 0 && entry.Amount == 100.50m);
        Assert.Contains(transaction.LedgerEntries, entry => entry.Type == 1 && entry.Amount == 100.50m);
        Assert.Equal(0, transaction.LedgerEntries.Sum(entry => entry.Type == 0 ? entry.Amount : -entry.Amount));
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsSameTransaction()
    {
        var key = Guid.NewGuid().ToString();
        var payload = new { fromAccount = "customer-001", toAccount = "merchant-001", amount = 25m, currency = "USD" };

        using var first = new HttpRequestMessage(HttpMethod.Post, "/transactions") { Content = JsonContent.Create(payload) };
        first.Headers.Add("Idempotency-Key", key);
        using var second = new HttpRequestMessage(HttpMethod.Post, "/transactions") { Content = JsonContent.Create(payload) };
        second.Headers.Add("Idempotency-Key", key);

        var firstResponse = await client.SendAsync(first);
        var secondResponse = await client.SendAsync(second);
        var firstTransaction = await firstResponse.Content.ReadFromJsonAsync<TransactionResponse>();
        var secondTransaction = await secondResponse.Content.ReadFromJsonAsync<TransactionResponse>();

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(firstTransaction!.Id, secondTransaction!.Id);
    }

    [Fact]
    public async Task InvalidTransaction_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new { fromAccount = "customer-001", toAccount = "merchant-001", amount = 0m })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record TransactionResponse(Guid Id, List<LedgerEntryResponse> LedgerEntries);
    private sealed record LedgerEntryResponse(string AccountId, int Type, decimal Amount, string Currency);
}
