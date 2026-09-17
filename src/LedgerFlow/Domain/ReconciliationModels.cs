namespace LedgerFlow.Domain;

public enum ReconciliationResultStatus
{
    Matched,
    MissingInternal,
    MissingExternal,
    AmountMismatch,
    CurrencyMismatch,
    DuplicateExternal
}

public sealed record ExternalSettlementRecord(
    string ExternalTransactionId,
    Guid TransactionId,
    decimal Amount,
    string Currency);

public sealed class ReconciliationRun
{
    private ReconciliationRun() { }

    public ReconciliationRun(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new DomainValidationException("Reconciliation idempotency key is required.");
        Id = Guid.NewGuid();
        IdempotencyKey = idempotencyKey.Trim();
        StartedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public List<ReconciliationResult> Results { get; private set; } = [];

    public void Complete(DateTimeOffset completedAt) => CompletedAt = completedAt;
}

public sealed class ReconciliationResult
{
    private ReconciliationResult() { }

    internal ReconciliationResult(
        Guid runId,
        ReconciliationResultStatus status,
        string? externalTransactionId,
        Guid? transactionId,
        decimal? internalAmount,
        decimal? externalAmount,
        string? internalCurrency,
        string? externalCurrency,
        string explanation)
    {
        Id = Guid.NewGuid();
        ReconciliationRunId = runId;
        Status = status;
        ExternalTransactionId = externalTransactionId;
        TransactionId = transactionId;
        InternalAmount = internalAmount;
        ExternalAmount = externalAmount;
        InternalCurrency = internalCurrency;
        ExternalCurrency = externalCurrency;
        Explanation = explanation;
    }

    public Guid Id { get; private set; }
    public Guid ReconciliationRunId { get; private set; }
    public ReconciliationResultStatus Status { get; private set; }
    public string? ExternalTransactionId { get; private set; }
    public Guid? TransactionId { get; private set; }
    public decimal? InternalAmount { get; private set; }
    public decimal? ExternalAmount { get; private set; }
    public string? InternalCurrency { get; private set; }
    public string? ExternalCurrency { get; private set; }
    public string Explanation { get; private set; } = null!;
}
