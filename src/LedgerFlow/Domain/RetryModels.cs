namespace LedgerFlow.Domain;

public enum RetryStatus
{
    Scheduled,
    DeadLettered,
    Recovered
}

public sealed class RetrySchedule
{
    private RetrySchedule() { }

    public RetrySchedule(Guid transactionId, int maxAttempts, DateTimeOffset nextAttemptAt)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        MaxAttempts = maxAttempts;
        NextAttemptAt = nextAttemptAt;
        Status = RetryStatus.Scheduled;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastFailureReason { get; private set; }
    public RetryStatus Status { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public void RecordFailure(int attemptCount, string failureReason, DateTimeOffset? nextAttemptAt)
    {
        AttemptCount = attemptCount;
        LastFailureReason = failureReason;
        NextAttemptAt = nextAttemptAt;
        Status = nextAttemptAt.HasValue ? RetryStatus.Scheduled : RetryStatus.DeadLettered;
        ResolvedAt = nextAttemptAt.HasValue ? null : DateTimeOffset.UtcNow;
    }

    public void MarkRecovered(DateTimeOffset resolvedAt)
    {
        Status = RetryStatus.Recovered;
        NextAttemptAt = null;
        ResolvedAt = resolvedAt;
    }
}

public sealed class DeadLetterRecord
{
    private DeadLetterRecord() { }

    public DeadLetterRecord(Guid transactionId, int retryAttempts, string failureReason, DateTimeOffset failedAt)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        RetryAttempts = retryAttempts;
        FailureReason = failureReason;
        FailedAt = failedAt;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public int RetryAttempts { get; private set; }
    public string FailureReason { get; private set; } = null!;
    public DateTimeOffset FailedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public void MarkResolved(DateTimeOffset resolvedAt) => ResolvedAt = resolvedAt;
}
