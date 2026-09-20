using LedgerFlow.Application;
using LedgerFlow.Domain;
using Xunit;
namespace LedgerFlow.Tests;
public sealed class SettlementTests
{
 [Fact] public void CreatesDeterministicSingleCurrencyBatchAndIsIdempotent(){var service=new SettlementService();var items=new[]{new SettlementItem(Guid.NewGuid(),100m,"usd"),new SettlementItem(Guid.NewGuid(),25m,"USD")};var first=service.CreateBatch(items,"settlement-001");var second=service.CreateBatch(items,"settlement-001");Assert.Equal(first.Id,second.Id);Assert.Equal(125m,first.TotalAmount);Assert.Equal("USD",first.Items[0].Currency);}
 [Fact] public void SupportsSuccessfulAndFailedLifecycle(){var service=new SettlementService();var successful=service.CreateBatch([new SettlementItem(Guid.NewGuid(),50m,"USD")],"settlement-success");var failed=service.CreateBatch([new SettlementItem(Guid.NewGuid(),75m,"USD")],"settlement-failure");Assert.Equal(SettlementBatchStatus.Settled,service.Process(successful.Id));Assert.Equal(SettlementBatchStatus.Failed,service.Process(failed.Id,true,"External settlement unavailable."));}
}