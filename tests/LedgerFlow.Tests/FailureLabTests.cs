using System.Net;
using System.Net.Http.Json;
using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class FailureLabTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory factory;
    private readonly HttpClient client;

    public FailureLabTests(TestingWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task DuplicateTransactionRequest_CreatesOneFinancialEffect()
    {
        var key = Guid.NewGuid().ToString();
        var payload = new { fromAccount = "failure-dup-source", toAccount = "failure-dup-destination", amount = 125m, currency = "USD" };

        var first = new HttpRequestMessage(HttpMethod.Post, "/transactions") { Content = JsonContent.Create(payload) };
        first.Headers.Add("Idempotency-Key", key);
        var second = new HttpRequestMessage(HttpMethod.Post, "/transactions") { Content = JsonContent.Create(payload) };
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
    public async Task ConcurrentDuplicateRequests_ReturnOneTransaction()
    {
        var key = Guid.NewGuid().ToString();
        var tasks = Enumerable.Range(0, 8).Select(_ => SendCreateAsync(key)).ToArray();

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Single(responses.Select(response => response.TransactionId).Distinct());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        Assert.Equal(1, await db.Transactions.CountAsync(item => item.IdempotencyKey == key));
    }

    [Fact]
    public async Task DuplicateEventDelivery_IsProcessedOnce()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var consumer = scope.ServiceProvider.GetRequiredService<IEventConsumer>();
        var transactionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var transactionEvent = new TransactionEvent(eventId, transactionId, TransactionStatus.Completed, DateTimeOffset.UtcNow, null);

        await consumer.ConsumeAsync(transactionEvent, CancellationToken.None);
        await consumer.ConsumeAsync(transactionEvent, CancellationToken.None);

        Assert.Equal(1, await db.ProcessedEvents.CountAsync(item => item.Id == eventId));
    }

    [Fact]
    public async Task OutOfOrderEventDelivery_DoesNotDuplicateProcessing()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var consumer = scope.ServiceProvider.GetRequiredService<IEventConsumer>();
        var transactionId = Guid.NewGuid();

        await consumer.ConsumeAsync(new TransactionEvent(Guid.NewGuid(), transactionId, TransactionStatus.Completed, DateTimeOffset.UtcNow.AddMinutes(1), null), CancellationToken.None);
        await consumer.ConsumeAsync(new TransactionEvent(Guid.NewGuid(), transactionId, TransactionStatus.Pending, DateTimeOffset.UtcNow, null), CancellationToken.None);

        Assert.Equal(2, await db.ProcessedEvents.CountAsync(item => item.AggregateId == transactionId));
    }

    [Fact]
    public async Task FailedProcessing_FollowedByRetry_RecoversWithoutNewLedgerEntries()
    {
        await using var db = NewDb();
        var retryService = new RetryService(db);
        var transaction = Transaction.Create("failure-retry-source", "failure-retry-destination", 40m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        var ledgerEntryCount = transaction.LedgerEntries.Count;
        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Failed, "Transient processor outage");
        await retryService.RecordFailureAsync(transaction, "Transient processor outage", CancellationToken.None);

        var schedule = await db.RetrySchedules.SingleAsync(item => item.TransactionId == transaction.Id);
        await Task.Delay(1100);
        await retryService.RetryNowAsync(transaction.Id, CancellationToken.None);

        Assert.Equal(TransactionStatus.Processing, transaction.Status);
        Assert.Equal(ledgerEntryCount, transaction.LedgerEntries.Count);
        Assert.Equal(RetryStatus.Recovered, schedule.Status);
    }

    [Fact]
    public async Task PermanentFailure_IsRoutedToDeadLetter()
    {
        await using var db = NewDb();
        var retryService = new RetryService(db);
        var transaction = Transaction.Create("failure-dlq-source", "failure-dlq-destination", 50m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        for (var attempt = 0; attempt < 3; attempt++)
            await retryService.RecordFailureAsync(transaction, $"Permanent failure {attempt + 1}", CancellationToken.None);

        var schedule = await db.RetrySchedules.SingleAsync(item => item.TransactionId == transaction.Id);
        var deadLetter = await db.DeadLetterRecords.SingleAsync(item => item.TransactionId == transaction.Id);

        Assert.Equal(RetryStatus.DeadLettered, schedule.Status);
        Assert.Equal(3, deadLetter.RetryAttempts);
    }

    [Fact]
    public async Task MissingEvent_LeavesOutboxMessageObservable()
    {
        using var scope = factory.Services.CreateScope();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new { fromAccount = "failure-missing-source", toAccount = "failure-missing-destination", amount = 60m, currency = "USD" })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await this.client.SendAsync(request);
        var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var outbox = await db.OutboxMessages.SingleAsync(item => item.AggregateId == transaction!.Id);
        Assert.Null(outbox.PublishedAt);
    }

    [Fact]
    public async Task DuplicateExternalSettlementRecord_IsDetected()
    {
        await using var db = NewDb();
        var service = new ReconciliationService(db);
        var transaction = CompletedTransaction(80m, "USD", "failure-duplicate-external");
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        var records = new[]
        {
            new ExternalSettlementRecord("external-duplicate", transaction.Id, 80m, "USD"),
            new ExternalSettlementRecord("external-duplicate", transaction.Id, 80m, "USD")
        };

        var run = await service.ReconcileAsync(records, "failure-reconciliation-duplicate", CancellationToken.None);

        Assert.Equal(2, run.Results.Count(result => result.Status == ReconciliationResultStatus.DuplicateExternal));
    }

    [Fact]
    public async Task LedgerAmountMismatch_IsDetected()
    {
        await using var db = NewDb();
        var service = new ReconciliationService(db);
        var transaction = CompletedTransaction(90m, "USD", "failure-amount-mismatch");
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        var run = await service.ReconcileAsync(
            new[] { new ExternalSettlementRecord("external-mismatch", transaction.Id, 91m, "USD") },
            "failure-reconciliation-amount",
            CancellationToken.None);

        var result = Assert.Single(run.Results);
        Assert.Equal(ReconciliationResultStatus.AmountMismatch, result.Status);
        Assert.Equal(90m, result.InternalAmount);
        Assert.Equal(91m, result.ExternalAmount);
    }

    [Fact]
    public void PartialSettlementFailure_LeavesOnlyCompletedItemsSettled()
    {
        var service = new SettlementService();
        var batch = service.CreateBatch(
            new[]
            {
                new SettlementItem(Guid.NewGuid(), 10m, "USD"),
                new SettlementItem(Guid.NewGuid(), 20m, "USD"),
                new SettlementItem(Guid.NewGuid(), 30m, "USD")
            },
            Guid.NewGuid().ToString());

        var processed = service.Process(batch.Id, failAfter: 1, failureReason: "Settlement provider timed out");

        Assert.Equal(SettlementBatchStatus.Failed, processed.Status);
        Assert.Equal(1, processed.Items.Count(item => item.Status == SettlementItemStatus.Settled));
        Assert.Equal(1, processed.Items.Count(item => item.Status == SettlementItemStatus.Failed));
        Assert.Equal(1, processed.Items.Count(item => item.Status == SettlementItemStatus.Pending));

        var repeated = service.Process(batch.Id, failAfter: 1, failureReason: "Should not run twice");
        Assert.Same(processed, repeated);
        Assert.Equal(1, repeated.Items.Count(item => item.Status == SettlementItemStatus.Settled));
    }

    [Fact]
    public async Task SuccessfulRecoveryAfterTransientFailure_DoesNotDuplicateLedgerEntries()
    {
        await using var db = NewDb();
        var retryService = new RetryService(db);
        var transaction = Transaction.Create("failure-recovery-source", "failure-recovery-destination", 110m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Failed, "Transient infrastructure failure");
        await retryService.RecordFailureAsync(transaction, "Transient infrastructure failure", CancellationToken.None);
        await retryService.MarkRecoveredAsync(transaction.Id, CancellationToken.None);
        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Completed);
        await db.SaveChangesAsync();

        Assert.Equal(TransactionStatus.Completed, transaction.Status);
        Assert.Equal(2, transaction.LedgerEntries.Count);
        Assert.Equal(2, await db.LedgerEntries.CountAsync(item => item.TransactionId == transaction.Id));
    }

    private async Task<CreateResponse> SendCreateAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new { fromAccount = "failure-concurrent-source", toAccount = "failure-concurrent-destination", amount = 75m, currency = "USD" })
        };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>();
        return new CreateResponse(response.StatusCode, transaction!.Id);
    }

    private static Transaction CompletedTransaction(decimal amount, string currency, string key)
    {
        var transaction = Transaction.Create($"source-{key}", $"destination-{key}", amount, currency, key);
        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Completed);
        return transaction;
    }

    private static LedgerFlowDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<LedgerFlowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LedgerFlowDbContext(options);
    }

    private sealed record TransactionResponse(Guid Id, TransactionStatus Status);
    private sealed record CreateResponse(HttpStatusCode StatusCode, Guid TransactionId);
}
