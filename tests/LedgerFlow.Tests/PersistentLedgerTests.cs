using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LedgerFlow.Tests;

public sealed class PersistentLedgerTests
{
    [Fact]
    public async Task CreateTransaction_PersistsTransactionAndLedgerEntries()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<LedgerFlowDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        Guid transactionId;

        await using (var writeDb = new LedgerFlowDbContext(options))
        {
            var service = new TransactionService(writeDb, new TransactionRepository(writeDb));
            var result = await service.CreateAsync(
                new CreateTransactionCommand("customer-001", "merchant-001", 42.75m, "USD", "order-20001"),
                CancellationToken.None);

            transactionId = result.Transaction.Id;
            Assert.False(result.AlreadyExisted);
        }

        await using (var readDb = new LedgerFlowDbContext(options))
        {
            var repository = new TransactionRepository(readDb);
            var transaction = await repository.GetByIdAsync(transactionId, CancellationToken.None);

            Assert.NotNull(transaction);
            Assert.Equal(42.75m, transaction!.Amount);
            Assert.Equal(2, transaction.LedgerEntries.Count);
            Assert.Equal(transaction.Amount, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Debit).Sum(entry => entry.Amount));
            Assert.Equal(transaction.Amount, transaction.LedgerEntries.Where(entry => entry.Type == EntryType.Credit).Sum(entry => entry.Amount));
        }
    }

    [Fact]
    public async Task CreateTransaction_SameIdempotencyKeyReturnsExistingRecord()
    {
        var options = new DbContextOptionsBuilder<LedgerFlowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new LedgerFlowDbContext(options);
        var service = new TransactionService(db, new TransactionRepository(db));
        var first = await service.CreateAsync(
            new CreateTransactionCommand("customer-001", "merchant-001", 15m, "USD", "order-20002"),
            CancellationToken.None);
        var second = await service.CreateAsync(
            new CreateTransactionCommand("customer-001", "merchant-001", 15m, "USD", "order-20002"),
            CancellationToken.None);

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Transaction.Id, second.Transaction.Id);
    }
}
