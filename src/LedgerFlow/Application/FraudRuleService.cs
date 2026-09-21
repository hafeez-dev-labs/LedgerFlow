using System.Collections.Concurrent;
using LedgerFlow.Domain;

namespace LedgerFlow.Application;

public sealed class FraudRuleService
{
    private readonly FraudRuleOptions options;
    private readonly ConcurrentQueue<FraudDecision> decisions = new();

    public FraudRuleService(FraudRuleOptions options)
    {
        this.options = options;
        if (options.TransactionThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.VelocityLimit <= 0) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public FraudDecision Evaluate(FraudEvaluationRequest request)
    {
        if (request.Amount <= 0) throw new DomainValidationException("Amount must be positive.");
        if (string.IsNullOrWhiteSpace(request.AccountId)) throw new DomainValidationException("AccountId is required.");
        if (request.RecentTransactionCount < 0) throw new DomainValidationException("RecentTransactionCount cannot be negative.");

        var reasons = new List<string>();
        var accountId = request.AccountId.Trim();

        if (request.Amount > options.TransactionThreshold)
            reasons.Add("Transaction amount exceeds the configured threshold.");

        if (request.RecentTransactionCount >= options.VelocityLimit)
            reasons.Add("Recent transaction count reached the configured velocity limit.");

        if (request.AccountRestricted || options.RestrictedAccounts?.Contains(accountId) == true)
            reasons.Add("Account is restricted by the configured account restriction rule.");

        var decision = new FraudDecision(
            Guid.NewGuid(),
            request.TransactionId,
            reasons.Count == 0 ? FraudDecisionStatus.Allowed : FraudDecisionStatus.Blocked,
            reasons,
            DateTimeOffset.UtcNow);

        decisions.Enqueue(decision);
        return decision;
    }

    public IReadOnlyList<FraudDecision> GetDecisions(Guid transactionId) =>
        decisions.Where(item => item.TransactionId == transactionId).ToArray();
}
