using System.Net;
using System.Net.Http.Json;
using LedgerFlow.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LedgerFlow.Tests;

public class TransactionApiTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly HttpClient client;

    public TransactionApiTests(TestingWebApplicationFactory factory)
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
    public async Task TransactionLifecycle_RecordsCompletedTransitions()
    {
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = "customer-002",
                toAccount = "merchant-002",
                amount = 50m,
                currency = "USD"
            })
        };
        createRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var createResponse = await client.SendAsync(createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<TransactionResponse>();

        var processingResponse = await client.PostAsJsonAsync(
            $"/transactions/{created!.Id}/transitions",
            new { status = TransactionStatus.Processing });
        var completedResponse = await client.PostAsJsonAsync(
            $"/transactions/{created.Id}/transitions",
            new { status = TransactionStatus.Completed });

        var transaction = await completedResponse.Content.ReadFromJsonAsync<TransactionResponse>();

        Assert.Equal(HttpStatusCode.OK, processingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
        Assert.NotNull(transaction);
        Assert.Equal(TransactionStatus.Completed, transaction!.Status);
        Assert.Equal(2, transaction.StateTransitions.Count);
        Assert.Equal(TransactionStatus.Pending, transaction.StateTransitions[0].FromStatus);
        Assert.Equal(TransactionStatus.Processing, transaction.StateTransitions[0].ToStatus);
        Assert.Equal(TransactionStatus.Processing, transaction.StateTransitions[1].FromStatus);
        Assert.Equal(TransactionStatus.Completed, transaction.StateTransitions[1].ToStatus);
    }

    [Fact]
    public async Task FailedTransaction_RequiresReasonAndBecomesTerminal()
    {
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = "customer-003",
                toAccount = "merchant-003",
                amount = 75m,
                currency = "USD"
            })
        };
        createRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var createResponse = await client.SendAsync(createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<TransactionResponse>();

        var processingResponse = await client.PostAsJsonAsync(
            $"/transactions/{created!.Id}/transitions",
            new { status = TransactionStatus.Processing });
        var failedResponse = await client.PostAsJsonAsync(
            $"/transactions/{created.Id}/transitions",
            new { status = TransactionStatus.Failed, failureReason = "Insufficient funds" });
        var repeatedResponse = await client.PostAsJsonAsync(
            $"/transactions/{created.Id}/transitions",
            new { status = TransactionStatus.Processing });

        var transaction = await failedResponse.Content.ReadFromJsonAsync<TransactionResponse>();

        Assert.Equal(HttpStatusCode.OK, processingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repeatedResponse.StatusCode);
        Assert.NotNull(transaction);
        Assert.Equal(TransactionStatus.Failed, transaction!.Status);
        Assert.Equal("Insufficient funds", transaction.StateTransitions[1].FailureReason);
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

    private sealed record TransactionResponse(
        Guid Id,
        TransactionStatus Status,
        List<LedgerEntryResponse> LedgerEntries,
        List<StateTransitionResponse> StateTransitions);

    private sealed record LedgerEntryResponse(string AccountId, int Type, decimal Amount, string Currency);
    private sealed record StateTransitionResponse(
        TransactionStatus FromStatus,
        TransactionStatus ToStatus,
        DateTimeOffset TransitionedAt,
        string? FailureReason);
}

public sealed class TestingWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}
