using LedgerFlow.Domain;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class TransactionDomainTests
{
    [Fact]
    public void CreateTransaction_CreatesPendingBalancedDoubleEntry()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100.50m, "usd", "order-10001");

        Assert.Equal(TransactionStatus.Pending, transaction.Status);
        Assert.Equal("USD", transaction.Currency);
        Assert.Equal(2, transaction.LedgerEntries.Count);
        Assert.Equal(100.50m, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Debit).Sum(entry => entry.Amount));
        Assert.Equal(100.50m, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Credit).Sum(entry => entry.Amount));
        Assert.Empty(transaction.StateTransitions);
    }

    [Fact]
    public void Transaction_AllowsPendingToProcessingToCompleted()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100m, "USD", "order-10001");

        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Completed);

        Assert.Equal(TransactionStatus.Completed, transaction.Status);
        Assert.Equal(2, transaction.StateTransitions.Count);
        Assert.Equal(TransactionStatus.Pending, transaction.StateTransitions[0].FromStatus);
        Assert.Equal(TransactionStatus.Processing, transaction.StateTransitions[0].ToStatus);
        Assert.Equal(TransactionStatus.Processing, transaction.StateTransitions[1].FromStatus);
        Assert.Equal(TransactionStatus.Completed, transaction.StateTransitions[1].ToStatus);
        Assert.All(transaction.StateTransitions, transition => Assert.NotEqual(default, transition.TransitionedAt));
    }

    [Fact]
    public void Transaction_AllowsProcessingToFailedWithReason()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100m, "USD", "order-10001");

        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Failed, "Account validation failed");

        var transition = Assert.Single(transaction.StateTransitions.Skip(1));
        Assert.Equal(TransactionStatus.Failed, transaction.Status);
        Assert.Equal("Account validation failed", transition.FailureReason);
    }

    [Theory]
    [InlineData(TransactionStatus.Pending, TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Pending, TransactionStatus.Failed)]
    [InlineData(TransactionStatus.Processing, TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Processing)]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Failed)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Processing)]
    public void Transaction_RejectsInvalidTransition(TransactionStatus current, TransactionStatus target)
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100m, "USD", "order-10001");
        if (current != TransactionStatus.Pending)
        {
            transaction.TransitionTo(TransactionStatus.Processing);
        }
        if (current is TransactionStatus.Completed or TransactionStatus.Failed)
        {
            transaction.TransitionTo(current, current == TransactionStatus.Failed ? "Processing failed" : null);
        }

        var statusBefore = transaction.Status;
        var historyBefore = transaction.StateTransitions.Count;

        Assert.Throws<DomainValidationException>(() => transaction.TransitionTo(target));
        Assert.Equal(statusBefore, transaction.Status);
        Assert.Equal(historyBefore, transaction.StateTransitions.Count);
    }

    [Fact]
    public void Transaction_RejectsFailureWithoutReason()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100m, "USD", "order-10001");
        transaction.TransitionTo(TransactionStatus.Processing);

        Assert.Throws<DomainValidationException>(() => transaction.TransitionTo(TransactionStatus.Failed));
        Assert.Equal(TransactionStatus.Processing, transaction.Status);
        Assert.Single(transaction.StateTransitions);
    }

    [Fact]
    public void Transaction_RejectsFailureReasonForNonFailedTransition()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100m, "USD", "order-10001");

        Assert.Throws<DomainValidationException>(() => transaction.TransitionTo(TransactionStatus.Processing, "not a failure"));
        Assert.Equal(TransactionStatus.Pending, transaction.Status);
        Assert.Empty(transaction.StateTransitions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateTransaction_RejectsNonPositiveAmount(decimal amount)
    {
        Assert.Throws<DomainValidationException>(() =>
            Transaction.Create("customer-001", "merchant-001", amount, "USD", "order-10001"));
    }

    [Fact]
    public void CreateTransaction_RejectsSameAccount()
    {
        Assert.Throws<DomainValidationException>(() =>
            Transaction.Create("customer-001", "customer-001", 10m, "USD", "order-10001"));
    }
}
