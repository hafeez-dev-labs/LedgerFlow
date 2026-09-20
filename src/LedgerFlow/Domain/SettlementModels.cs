namespace LedgerFlow.Domain;

public enum SettlementBatchStatus { Pending, Processing, Settled, Failed }
public sealed record SettlementItem(Guid TransactionId, decimal Amount, string Currency);
public sealed class SettlementBatch
{
    private SettlementBatch() { }
    public SettlementBatch(string idempotencyKey, IReadOnlyCollection<SettlementItem> items)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new DomainValidationException("Settlement idempotency key is required.");
        if (items.Count == 0) throw new DomainValidationException("Settlement batch must contain at least one item.");
        var currency = items.First().Currency.Trim().ToUpperInvariant();
        if (items.Any(item => item.Amount <= 0 || !string.Equals(item.Currency.Trim(), currency, StringComparison.OrdinalIgnoreCase))) throw new DomainValidationException("Settlement items must be positive and use one currency.");
        Id=Guid.NewGuid(); IdempotencyKey=idempotencyKey.Trim(); Status=SettlementBatchStatus.Pending; Items=items.Select(item=>item with {Currency=currency}).ToList(); CreatedAt=DateTimeOffset.UtcNow; TotalAmount=Items.Sum(item=>item.Amount);
    }
    public Guid Id {get;private set;} public string IdempotencyKey {get;private set;}=null!; public SettlementBatchStatus Status {get;private set;} public List<SettlementItem> Items {get;private set;}=[]; public DateTimeOffset CreatedAt {get;private set;} public DateTimeOffset? CompletedAt {get;private set;} public decimal TotalAmount {get;private set;} public string? FailureReason {get;private set;}
    public void Start(){if(Status!=SettlementBatchStatus.Pending)throw new DomainValidationException("Only pending batches can start.");Status=SettlementBatchStatus.Processing;}
    public void Settle(DateTimeOffset at){if(Status!=SettlementBatchStatus.Processing)throw new DomainValidationException("Only processing batches can settle.");Status=SettlementBatchStatus.Settled;CompletedAt=at;}
    public void Fail(string reason){if(Status!=SettlementBatchStatus.Processing)throw new DomainValidationException("Only processing batches can fail.");if(string.IsNullOrWhiteSpace(reason))throw new DomainValidationException("Settlement failure reason is required.");Status=SettlementBatchStatus.Failed;FailureReason=reason.Trim();}
}