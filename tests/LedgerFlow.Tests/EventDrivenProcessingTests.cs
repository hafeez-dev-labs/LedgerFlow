using System.Net;
using System.Net.Http.Json;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LedgerFlow.Tests;

public class EventDrivenProcessingTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory factory;
    private readonly HttpClient client;

    public EventDrivenProcessingTests(TestingWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateTransaction_PersistsTransactionAndOutboxMessageTogether()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = "event-source-001",
                toAccount = "event-destination-001",
                amount = 40m,
                currency = "USD"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(transaction);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var storedTransaction = await db.Transactions.FindAsync(transaction!.Id);
        var outboxMessage = Assert.Single(db.OutboxMessages.Where(message => message.AggregateId == transaction.Id));

        Assert.NotNull(storedTransaction);
        Assert.Null(outboxMessage.PublishedAt);
        Assert.Equal(transaction.Id, outboxMessage.AggregateId);
        Assert.Equal(nameof(TransactionEvent), outboxMessage.EventType);
    }

    [Fact]
    public async Task OutboxPublisher_PublishesPendingEventAndMarksItPublished()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = JsonContent.Create(new
            {
                fromAccount = "event-source-002",
                toAccount = "event-destination-002",
                amount = 55m,
                currency = "USD"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>();

        using var scope = factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var broker = scope.ServiceProvider.GetRequiredService<IEventBroker>();
        var published = await publisher.PublishPendingAsync(CancellationToken.None);

        Assert.True(published >= 1);

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        TransactionEvent? transactionEvent = null;
        await foreach (var candidate in broker.ConsumeAsync(cancellation.Token))
        {
            if (candidate.TransactionId == transaction!.Id)
            {
                transactionEvent = candidate;
                break;
            }
        }

        Assert.NotNull(transactionEvent);
        Assert.Equal(TransactionStatus.Pending, transactionEvent!.Status);

        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var outboxMessage = Assert.Single(db.OutboxMessages.Where(message => message.AggregateId == transaction.Id));
        Assert.NotNull(outboxMessage.PublishedAt);
    }

    [Fact]
    public async Task Consumer_IgnoresDuplicateAndAcceptsOutOfOrderEventsWithoutDuplicateProcessing()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var consumer = scope.ServiceProvider.GetRequiredService<IEventConsumer>();
        var transactionId = Guid.NewGuid();
        var completedEvent = new TransactionEvent(Guid.NewGuid(), transactionId, TransactionStatus.Completed, DateTimeOffset.UtcNow.AddMinutes(1), null);
        var pendingEvent = new TransactionEvent(Guid.NewGuid(), transactionId, TransactionStatus.Pending, DateTimeOffset.UtcNow, null);

        await consumer.ConsumeAsync(completedEvent, CancellationToken.None);
        await consumer.ConsumeAsync(completedEvent, CancellationToken.None);
        await consumer.ConsumeAsync(pendingEvent, CancellationToken.None);

        Assert.Equal(2, db.ProcessedEvents.Count(item => item.AggregateId == transactionId));
    }

    private sealed record TransactionResponse(Guid Id, TransactionStatus Status);
}
