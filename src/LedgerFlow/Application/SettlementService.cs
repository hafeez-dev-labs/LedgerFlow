using LedgerFlow.Domain;

namespace LedgerFlow.Application;

public sealed class SettlementService(IAuditTrailWriter? auditTrail = null)
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SettlementBatch> Batches = new(StringComparer.Ordinal);

    public SettlementBatch CreateBatch(IReadOnlyCollection<SettlementItem> items, string idempotencyKey)
    {
        using var activity = LedgerFlow.Infrastructure.LedgerFlowTelemetry.StartActivity("settlement.create");
        lock (Gate)
        {
            var key = idempotencyKey?.Trim() ?? string.Empty;
            if (Batches.TryGetValue(key, out var existing))
            {
                LedgerFlow.Infrastructure.LedgerFlowTelemetry.Add(
                    LedgerFlow.Infrastructure.LedgerFlowTelemetry.Settlements,
                    "outcome",
                    "idempotent");
                return existing;
            }

            var batch = new SettlementBatch(key, items);
            Batches[key] = batch;
            LedgerFlow.Infrastructure.LedgerFlowTelemetry.Add(
                LedgerFlow.Infrastructure.LedgerFlowTelemetry.Settlements,
                "outcome",
                "created");
            auditTrail?.Record("settlement.created", "settlement", batch.Id, null, key, new { itemCount = batch.Items.Count, batch.TotalAmount });
            return batch;
        }
    }

    public SettlementBatch Process(Guid id, bool fail = false, string? failureReason = null)
    {
        using var activity = LedgerFlow.Infrastructure.LedgerFlowTelemetry.StartActivity("settlement.process", id);

        lock (Gate)
        {
            var batch = Batches.Values.SingleOrDefault(value => value.Id == id)
                ?? throw new DomainValidationException("Settlement batch was not found.");

            if (batch.Status is SettlementBatchStatus.Settled or SettlementBatchStatus.Failed)
                return batch;

            batch.Start();

            if (fail)
            {
                batch.Fail(failureReason ?? "Simulated settlement failure.");
                LedgerFlow.Infrastructure.LedgerFlowTelemetry.Add(
                    LedgerFlow.Infrastructure.LedgerFlowTelemetry.Settlements,
                    "outcome",
                    "failed");
                auditTrail?.Record("settlement.failed", "settlement", batch.Id, null, LedgerFlow.Infrastructure.LedgerFlowTelemetry.CurrentCorrelationId ?? batch.IdempotencyKey, new { batch.TotalAmount, batch.FailureReason });
            }
            else
            {
                batch.Settle(DateTimeOffset.UtcNow);
                LedgerFlow.Infrastructure.LedgerFlowTelemetry.Add(
                    LedgerFlow.Infrastructure.LedgerFlowTelemetry.Settlements,
                    "outcome",
                    "settled");
                auditTrail?.Record("settlement.settled", "settlement", batch.Id, null, LedgerFlow.Infrastructure.LedgerFlowTelemetry.CurrentCorrelationId ?? batch.IdempotencyKey, new { batch.TotalAmount, batch.CompletedAt });
            }

            return batch;
        }
    }

    public SettlementBatch? Get(Guid id)
    {
        lock (Gate)
            return Batches.Values.SingleOrDefault(value => value.Id == id);
    }
}
