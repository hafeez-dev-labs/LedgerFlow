using System.Net;
using System.Net.Http.Json;
using LedgerFlow.Domain;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class AuditTrailApiTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly HttpClient client;

    public AuditTrailApiTests(TestingWebApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task TransactionLifecycleAndFraudEvaluationProduceAppendOnlyAuditRecords()
    {
        var key = Guid.NewGuid().ToString();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = $"customer-{Guid.NewGuid():N}",
                toAccount = $"merchant-{Guid.NewGuid():N}",
                amount = 50m,
                currency = "USD"
            })
        };
        createRequest.Headers.Add("Idempotency-Key", key);

        var createResponse = await client.SendAsync(createRequest);
        Assert.True(createResponse.Headers.TryGetValues("X-Correlation-Id", out var correlationValues));
        Assert.False(string.IsNullOrWhiteSpace(correlationValues!.Single()));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TransactionResponse>();
        Assert.NotNull(created);

        var processingResponse = await client.PostAsJsonAsync(
            $"/transactions/{created!.Id}/transitions",
            new { status = TransactionStatus.Processing });
        var completedResponse = await client.PostAsJsonAsync(
            $"/transactions/{created.Id}/transitions",
            new { status = TransactionStatus.Completed });
        Assert.Equal(HttpStatusCode.OK, processingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);

        var fraudResponse = await client.PostAsJsonAsync(
            "/fraud/evaluate",
            new
            {
                transactionId = created.Id,
                accountId = "audit-test",
                amount = 50m,
                recentTransactionCount = 0
            });
        Assert.Equal(HttpStatusCode.OK, fraudResponse.StatusCode);

        var auditResponse = await client.GetAsync($"/audit/transactions/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var records = await auditResponse.Content.ReadFromJsonAsync<List<AuditRecordResponse>>();

        Assert.NotNull(records);
        Assert.Equal(
            new[]
            {
                "transaction.created",
                "ledger.posted",
                "transaction.transitioned",
                "transaction.transitioned",
                "fraud.decision"
            },
            records!.Select(record => record.EventType).ToArray());
        Assert.All(records, record => Assert.Equal(created.Id, record.TransactionId));
        Assert.Equal(records.Count, records.Select(record => record.Id).Distinct().Count());
        Assert.True(records.Select(record => record.OccurredAt).SequenceEqual(
            records.Select(record => record.OccurredAt).OrderBy(value => value)));

    }

    private sealed record TransactionResponse(Guid Id, TransactionStatus Status);
    [Fact]
    public async Task HealthRequestReturnsCorrelationId()
    {
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    private sealed record AuditRecordResponse(
        Guid Id,
        string EventType,
        string AggregateType,
        Guid AggregateId,
        Guid? TransactionId,
        string? CorrelationId,
        DateTimeOffset OccurredAt,
        string Payload);
}
