using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Infrastructure;

public sealed class InMemoryEventBroker : IEventBroker
{
    private readonly Channel<TransactionEvent> channel = Channel.CreateUnbounded<TransactionEvent>();

    public ValueTask PublishAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(transactionEvent, cancellationToken);

    public IAsyncEnumerable<TransactionEvent> ConsumeAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class TransactionEventConsumer(LedgerFlowDbContext db) : IEventConsumer
{
    public async Task ConsumeAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken)
    {
        if (await db.ProcessedEvents.AnyAsync(item => item.Id == transactionEvent.EventId, cancellationToken))
        {
            return;
        }

        db.ProcessedEvents.Add(new ProcessedEvent(
            transactionEvent.EventId,
            nameof(TransactionEvent),
            transactionEvent.TransactionId,
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OutboxPublisher(
    LedgerFlowDbContext db,
    IEventBroker broker)
{
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var messages = await db.OutboxMessages
            .Where(message => message.PublishedAt == null)
            .OrderBy(message => message.OccurredAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            await broker.PublishAsync(message.Deserialize(), cancellationToken);
            message.MarkPublished(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        return messages.Count;
    }
}

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
                await publisher.PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to publish pending outbox messages.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class EventConsumerWorker(
    IEventBroker broker,
    IServiceScopeFactory scopeFactory,
    ILogger<EventConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var transactionEvent in broker.ConsumeAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var consumer = scope.ServiceProvider.GetRequiredService<IEventConsumer>();
                await consumer.ConsumeAsync(transactionEvent, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to consume transaction event {EventId}.", transactionEvent.EventId);
            }
        }
    }
}
