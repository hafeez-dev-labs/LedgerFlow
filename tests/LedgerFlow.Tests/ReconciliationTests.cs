using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public async Task ReconcileDetectsMatchesAndRequiredDiscrepancies()
    {
        await using var db = CreateDb();
        var service = new ReconciliationService(db);

        var matched = CreateCompletedTransaction(100m, "USD", "recon-matched");
        var amountMismatch = CreateCompletedTransaction(200m, "USD", "recon-amount");
        var currencyMismatch = CreateCompletedTransaction(300m, "USD", "recon-currency");
        var missingExternal = CreateCompletedTransaction(400m, "USD", "recon-missing-external");
        db.Transactions.AddRange(matched, amountMismatch, currencyMismatch, missingExternal);
        await db.SaveChangesAsync();

        var unknownId = Guid.NewGuid();
        var records = new[]
        {
            new ExternalSettlementRecord("ext-matched", matched.Id, 100m, "USD"),
            new ExternalSettlementRecord("ext-amount", amountMismatch.Id, 250m, "USD"),
            new ExternalSettlementRecord("ext-currency", currencyMismatch.Id, 300m, "EUR"),
            new ExternalSettlementRecord("ext-missing-internal", unknownId, 50m, "USD"),
            new ExternalSettlementRecord("ext-duplicate", missingExternal.Id, 400m, "USD"),
            new ExternalSettlementRecord("ext-duplicate", missingExternal.Id, 400m, "USD")
        };

        var run = await service.ReconcileAsync(records, "reconciliation-001", CancellationToken.None);

        Assert.Equal(6, run.Results.Count);
        Assert.Contains(run.Results, result => result.Status == ReconciliationResultStatus.Matched && result.TransactionId == matched.Id);
        Assert.Contains(run.Results, result => result.Status == ReconciliationResultStatus.AmountMismatch && result.TransactionId == amountMismatch.Id);
        Assert.Contains(run.Results, result => result.Status == ReconciliationResultStatus.CurrencyMismatch && result.TransactionId == currencyMismatch.Id);
        Assert.Contains(run.Results, result => result.Status == ReconciliationResultStatus.MissingInternal && result.TransactionId == unknownId);
        Assert.Equal(2, run.Results.Count(result => result.Status == ReconciliationResultStatus.DuplicateExternal));
        Assert.Contains(run.Results, result => result.Status == ReconciliationResultStatus.MissingExternal && result.TransactionId == missingExternal.Id);
    }

    [Fact]
    public async Task SameReconciliationKeyReturnsExistingRun()
    {
        await using var db = CreateDb();
        var service = new ReconciliationService(db);
        var transaction = CreateCompletedTransaction(75m, "USD", "recon-idempotent");
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        var records = new[] { new ExternalSettlementRecord("ext-idempotent", transaction.Id, 75m, "USD") };
        var first = await service.ReconcileAsync(records, "reconciliation-002", CancellationToken.None);
        var second = await service.ReconcileAsync(records, "reconciliation-002", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(second.Results);
        Assert.Equal(ReconciliationResultStatus.Matched, second.Results[0].Status);
    }

    private static Transaction CreateCompletedTransaction(decimal amount, string currency, string key)
    {
        var transaction = Transaction.Create($"source-{key}", $"destination-{key}", amount, currency, key);
        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Completed);
        return transaction;
    }

    private static LedgerFlowDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LedgerFlowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LedgerFlowDbContext(options);
    }
}
