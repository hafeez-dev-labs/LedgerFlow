using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LedgerFlow.Infrastructure;

public static class LedgerFlowTelemetry
{
    public const string ServiceName = "LedgerFlow";
    public const string ActivitySourceName = "LedgerFlow";
    public const string MeterName = "LedgerFlow";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> Transactions = Meter.CreateCounter<long>(
        "ledgerflow.transactions",
        description: "Number of transaction operations by outcome.");

    public static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "ledgerflow.retries",
        description: "Number of transaction retry and recovery operations.");

    public static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(
        "ledgerflow.dead_letters",
        description: "Number of transactions moved to dead-letter state.");

    public static readonly Counter<long> ReconciliationResults = Meter.CreateCounter<long>(
        "ledgerflow.reconciliation.results",
        description: "Number of reconciliation results by status.");

    public static readonly Counter<long> Settlements = Meter.CreateCounter<long>(
        "ledgerflow.settlements",
        description: "Number of settlement operations by outcome.");

    public static readonly Counter<long> FraudDecisions = Meter.CreateCounter<long>(
        "ledgerflow.fraud.decisions",
        description: "Number of fraud-rule decisions by outcome.");

    public static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(
        "ledgerflow.processing.duration",
        unit: "ms",
        description: "Duration of transaction processing operations.");

    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "ledgerflow.operation.duration",
        unit: "ms",
        description: "Duration of business operations.");

    public static string? CurrentCorrelationId => Activity.Current?.TraceId.ToString();

    public static Activity? StartActivity(string name, Guid? transactionId = null)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        if (activity is null)
            return null;

        if (transactionId.HasValue)
            activity.SetTag("ledgerflow.transaction_id", transactionId.Value);

        var correlationId = CurrentCorrelationId;
        if (!string.IsNullOrWhiteSpace(correlationId))
            activity.SetTag("correlation.id", correlationId);

        return activity;
    }

    public static void Add(Counter<long> counter, string key, string value) =>
        counter.Add(1, new KeyValuePair<string, object?>(key, value));

    public static void Record(Histogram<double> histogram, double milliseconds, string key, string value) =>
        histogram.Record(milliseconds, new KeyValuePair<string, object?>(key, value));
}
