using System.Text.Json;
using LedgerFlow.Domain;

namespace LedgerFlow.Infrastructure;

public sealed record TransactionEvent(
    Guid EventId,
    Guid TransactionId,
    TransactionStatus Status,
    DateTimeOffset OccurredAt,
    string? FailureReason);

public interface IEventBroker
{
    ValueTask PublishAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken);
    IAsyncEnumerable<TransactionEvent> ConsumeAsync(CancellationToken cancellationToken);
}

public interface IEventConsumer
{
    Task ConsumeAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken);
}

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(TransactionEvent transactionEvent)
    {
        Id = transactionEvent.EventId;
        EventType = nameof(TransactionEvent);
        AggregateId = transactionEvent.TransactionId;
        Payload = JsonSerializer.Serialize(transactionEvent);
        OccurredAt = transactionEvent.OccurredAt;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public string Payload { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    public TransactionEvent Deserialize() =>
        JsonSerializer.Deserialize<TransactionEvent>(Payload)
        ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{Id}'.");

    public void MarkPublished(DateTimeOffset publishedAt) => PublishedAt = publishedAt;
}

public sealed class ProcessedEvent
{
    private ProcessedEvent() { }

    public ProcessedEvent(Guid id, string eventType, Guid aggregateId, DateTimeOffset processedAt)
    {
        Id = id;
        EventType = eventType;
        AggregateId = aggregateId;
        ProcessedAt = processedAt;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }
}
