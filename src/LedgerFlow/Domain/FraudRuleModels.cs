namespace LedgerFlow.Domain;

public enum FraudDecisionStatus { Allowed, Blocked }

public sealed record FraudRuleOptions(decimal TransactionThreshold = 10000m, int VelocityLimit = 5, IReadOnlySet<string>? RestrictedAccounts = null);

public sealed record FraudEvaluationRequest(
    Guid TransactionId,
    string AccountId,
    decimal Amount,
    int RecentTransactionCount,
    bool AccountRestricted = false);

public sealed record FraudDecision(
    Guid DecisionId,
    Guid TransactionId,
    FraudDecisionStatus Status,
    IReadOnlyList<string> Reasons,
    DateTimeOffset EvaluatedAt);
