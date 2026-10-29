using LedgerFlow.Application;
using LedgerFlow.Domain;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class SettlementTests
{
    [Fact]
    public void CreatesDeterministicSingleCurrencyBatchAndIsIdempotent()
    {
        var service = new SettlementService();
        var items = new[]
        {
            new SettlementItem(Guid.NewGuid(), 100m, "usd"),
            new SettlementItem(Guid.NewGuid(), 25m, "USD")
        };

        var first = service.CreateBatch(items, "settlement-001");
        var second = service.CreateBatch(items, "settlement-001");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(125m, first.TotalAmount);
        Assert.Equal("USD", first.Items[0].Currency);
    }

    [Fact]
    public void SupportsSuccessfulAndFailedLifecycle()
    {
        var service = new SettlementService();
        var successful = service.CreateBatch([new SettlementItem(Guid.NewGuid(), 50m, "USD")], "settlement-success");
        var failed = service.CreateBatch([new SettlementItem(Guid.NewGuid(), 75m, "USD")], "settlement-failure");

        Assert.Equal(SettlementBatchStatus.Settled, service.Process(successful.Id).Status);
        Assert.Equal(SettlementBatchStatus.Failed, service.Process(failed.Id, true, "External settlement unavailable.").Status);
    }

    [Fact]
    public void RetrySettlesOnlyOutstandingItemsAndKeepsSettledItemsSettled()
    {
        var service = new SettlementService();
        var batch = service.CreateBatch(
        [
            new SettlementItem(Guid.NewGuid(), 10m, "USD"),
            new SettlementItem(Guid.NewGuid(), 20m, "USD"),
            new SettlementItem(Guid.NewGuid(), 30m, "USD")
        ], "settlement-partial-retry");

        service.Process(batch.Id, failAfter: 1);
        var previouslySettled = batch.Items[0].TransactionId;

        var retried = service.Retry(batch.Id);

        Assert.Equal(SettlementBatchStatus.Settled, retried.Status);
        Assert.Equal(3, retried.Items.Count(item => item.Status == SettlementItemStatus.Settled));
        Assert.Equal(previouslySettled, retried.Items[0].TransactionId);
        Assert.Equal(SettlementItemStatus.Settled, retried.Items[0].Status);
        Assert.NotNull(retried.CompletedAt);
    }

    [Fact]
    public void RetryRejectsPendingOrAlreadySettledBatches()
    {
        var service = new SettlementService();
        var pending = service.CreateBatch([new SettlementItem(Guid.NewGuid(), 10m, "USD")], "settlement-pending-retry");
        var settled = service.CreateBatch([new SettlementItem(Guid.NewGuid(), 15m, "USD")], "settlement-already-settled");

        service.Process(settled.Id);

        Assert.Throws<DomainValidationException>(() => service.Retry(pending.Id));
        Assert.Throws<DomainValidationException>(() => service.Retry(settled.Id));
    }
}
