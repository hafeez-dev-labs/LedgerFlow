using LedgerFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Infrastructure;

public interface ITransactionRepository
{
    Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Transaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
    void Add(Transaction transaction);
}

public sealed class TransactionRepository(LedgerFlowDbContext db) : ITransactionRepository
{
    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Transactions
            .Include(transaction => transaction.LedgerEntries)
            .SingleOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);

    public Task<Transaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Transactions
            .Include(transaction => transaction.LedgerEntries)
            .SingleOrDefaultAsync(transaction => transaction.IdempotencyKey == idempotencyKey, cancellationToken);

    public void Add(Transaction transaction) => db.Transactions.Add(transaction);
}
