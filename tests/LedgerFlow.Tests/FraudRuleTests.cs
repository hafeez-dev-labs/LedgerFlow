using LedgerFlow.Application;
using LedgerFlow.Domain;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class FraudRuleTests
{
    [Fact]
    public void AllowsTransactionWhenNoRuleMatches()
    {
        var service = new FraudRuleService(new FraudRuleOptions(1000m, 5));
        var transactionId = Guid.NewGuid();
        var decision = service.Evaluate(new FraudEvaluationRequest(transactionId, "acct-1", 250m, 2));

        Assert.Equal(FraudDecisionStatus.Allowed, decision.Status);
        Assert.Empty(decision.Reasons);
        Assert.Single(service.GetDecisions(transactionId));
    }

    [Fact]
    public void BlocksThresholdVelocityAndRestrictedAccount()
    {
        var service = new FraudRuleService(new FraudRuleOptions(1000m, 5, new HashSet<string> { "restricted-1" }));
        var transactionId = Guid.NewGuid();
        var decision = service.Evaluate(new FraudEvaluationRequest(transactionId, "restricted-1", 1500m, 5));

        Assert.Equal(FraudDecisionStatus.Blocked, decision.Status);
        Assert.Equal(3, decision.Reasons.Count);
        Assert.Equal(transactionId, decision.TransactionId);
    }

    [Fact]
    public void DoesNotMutateFinancialTransactionState()
    {
        var service = new FraudRuleService(new FraudRuleOptions(1000m, 5));
        var transactionId = Guid.NewGuid();
        var before = service.GetDecisions(transactionId).Count;

        _ = service.Evaluate(new FraudEvaluationRequest(transactionId, "acct-2", 1500m, 0));

        Assert.Equal(0, before);
        Assert.Single(service.GetDecisions(transactionId));
    }
}
