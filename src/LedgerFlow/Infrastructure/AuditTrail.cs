using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Infrastructure;

public sealed class AuditRecord
{
    private AuditRecord() { }

    public AuditRecord(
        string eventType,
        string aggregateType,
        Guid aggregateId,
        Guid? transactionId,
        string? correlationId,
        DateTimeOffset occurredAt,
        string payload)
    {
        Id = Guid.NewGuid();
        EventType = eventType;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        TransactionId = transactionId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        Payload = payload;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public string AggregateType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public Guid? TransactionId { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Payload { get; private set; } = null!;
}

public interface IAuditTrailWriter
{
    void Record(
        string eventType,
        string aggregateType,
        Guid aggregateId,
        Guid? transactionId,
        string? correlationId,
        object details);

    IReadOnlyList<AuditRecord> GetByTransaction(Guid transactionId);
}

public sealed class AuditTrailWriter(IDbContextFactory<LedgerFlowDbContext> factory) : IAuditTrailWriter
{
    public void Record(
        string eventType,
        string aggregateType,
        Guid aggregateId,
        Guid? transactionId,
        string? correlationId,
        object details)
    {
        using var db = factory.CreateDbContext();
        db.AuditRecords.Add(new AuditRecord(
            eventType,
            aggregateType,
            aggregateId,
            transactionId,
            correlationId,
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(details)));
        db.SaveChanges();
    }

    public IReadOnlyList<AuditRecord> GetByTransaction(Guid transactionId)
    {
        using var db = factory.CreateDbContext();
        return db.AuditRecords
            .AsNoTracking()
            .Where(record => record.TransactionId == transactionId)
            .OrderBy(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .ToArray();
    }
}
