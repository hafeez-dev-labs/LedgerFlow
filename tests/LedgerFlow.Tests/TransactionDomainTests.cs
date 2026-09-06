using LedgerFlow.Domain;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class TransactionDomainTests
{
    [Fact]
    public void CreateTransaction_CreatesBalancedDoubleEntry()
    {
        var transaction = Transaction.Create("customer-001", "merchant-001", 100.50m, "usd", "order-10001");

        Assert.Equal(TransactionStatus.Completed, transaction.Status);
        Assert.Equal("USD", transaction.Currency);
        Assert.Equal(2, transaction.LedgerEntries.Count);
        Assert.Equal(100.50m, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Debit).Sum(entry => entry.Amount));
        Assert.Equal(100.50m, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Credit).Sum(entry => entry.Amount));
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
