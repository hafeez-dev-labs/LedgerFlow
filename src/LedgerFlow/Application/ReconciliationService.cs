using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Application;

public sealed class ReconciliationService(LedgerFlowDbContext db, IAuditTrailWriter? auditTrail = null)
{
    public async Task<ReconciliationRun> ReconcileAsync(
        IReadOnlyCollection<ExternalSettlementRecord> externalRecords,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainValidationException("Reconciliation idempotency key is required.");

        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.ReconciliationRuns
            .Include(run => run.Results)
            .SingleOrDefaultAsync(run => run.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
            return existing;

        ValidateExternalRecords(externalRecords);

        var run = new ReconciliationRun(normalizedKey);
        var internalTransactions = await db.Transactions
            .Where(transaction => transaction.Status == TransactionStatus.Completed)
            .OrderBy(transaction => transaction.Id)
            .ToListAsync(cancellationToken);

        var duplicateExternalIds = externalRecords
            .GroupBy(record => record.ExternalTransactionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var matchedInternalIds = new HashSet<Guid>();

        foreach (var external in externalRecords.OrderBy(record => record.ExternalTransactionId, StringComparer.Ordinal).ThenBy(record => record.TransactionId))
        {
            if (duplicateExternalIds.Contains(external.ExternalTransactionId))
            {
                AddResult(run, ReconciliationResultStatus.DuplicateExternal, external, null, "External transaction identifier appears more than once.");
                continue;
            }

            var internalTransaction = internalTransactions.SingleOrDefault(transaction => transaction.Id == external.TransactionId);
            if (internalTransaction is null)
            {
                AddResult(run, ReconciliationResultStatus.MissingInternal, external, null, "External record references an internal transaction that does not exist or is not completed.");
                continue;
            }

            if (internalTransaction.Amount != external.Amount)
            {
                AddResult(run, ReconciliationResultStatus.AmountMismatch, external, internalTransaction, "Internal transaction amount differs from the external settlement amount.");
                continue;
            }

            if (!string.Equals(internalTransaction.Currency, external.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                AddResult(run, ReconciliationResultStatus.CurrencyMismatch, external, internalTransaction, "Internal transaction currency differs from the external settlement currency.");
                continue;
            }

            matchedInternalIds.Add(internalTransaction.Id);
            AddResult(run, ReconciliationResultStatus.Matched, external, internalTransaction, "Internal transaction matches the external settlement record.");
        }

        foreach (var internalTransaction in internalTransactions.Where(transaction => !matchedInternalIds.Contains(transaction.Id)))
        {
            if (run.Results.Any(result => result.TransactionId == internalTransaction.Id && result.Status != ReconciliationResultStatus.MissingExternal))
                continue;

            run.Results.Add(new ReconciliationResult(
                run.Id,
                ReconciliationResultStatus.MissingExternal,
                null,
                internalTransaction.Id,
                internalTransaction.Amount,
                null,
                internalTransaction.Currency,
                null,
                "Completed internal transaction has no matching external settlement record."));
        }

        run.Complete(DateTimeOffset.UtcNow);
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        var statusCounts = run.Results.GroupBy(result => result.Status).ToDictionary(group => group.Key.ToString(), group => group.Count());
        auditTrail?.Record("reconciliation.completed", "reconciliation", run.Id, null, normalizedKey, new { resultCount = run.Results.Count, statusCounts });
        return run;
    }

    public Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.ReconciliationRuns
            .Include(run => run.Results)
            .SingleOrDefaultAsync(run => run.Id == id, cancellationToken);

    private static void ValidateExternalRecords(IEnumerable<ExternalSettlementRecord> externalRecords)
    {
        foreach (var record in externalRecords)
        {
            if (string.IsNullOrWhiteSpace(record.ExternalTransactionId))
                throw new DomainValidationException("External transaction identifier is required.");
            if (record.Amount <= 0)
                throw new DomainValidationException("External settlement amount must be positive.");
            if (string.IsNullOrWhiteSpace(record.Currency) || record.Currency.Trim().Length != 3)
                throw new DomainValidationException("External settlement currency must be a 3-letter code.");
        }
    }

    private static void AddResult(
        ReconciliationRun run,
        ReconciliationResultStatus status,
        ExternalSettlementRecord external,
        Transaction? internalTransaction,
        string explanation)
    {
        run.Results.Add(new ReconciliationResult(
            run.Id,
            status,
            external.ExternalTransactionId,
            internalTransaction?.Id ?? (status == ReconciliationResultStatus.MissingInternal ? external.TransactionId : null),
            internalTransaction?.Amount,
            external.Amount,
            internalTransaction?.Currency,
            external.Currency.Trim().ToUpperInvariant(),
            explanation));
    }
}
