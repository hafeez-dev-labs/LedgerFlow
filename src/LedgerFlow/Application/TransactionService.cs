using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LedgerFlow.Application;

public sealed record CreateTransactionCommand(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    string IdempotencyKey);

public sealed record CreateTransactionResult(Transaction Transaction, bool AlreadyExisted);

public sealed class TransactionService(
    LedgerFlowDbContext db,
    ITransactionRepository transactions)
{
    public async Task<CreateTransactionResult> CreateAsync(CreateTransactionCommand command, CancellationToken cancellationToken)
    {
        ValidateRequest(command);

        IDbContextTransaction? databaseTransaction = null;
        if (!db.Database.IsInMemory())
        {
            databaseTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var normalizedKey = command.IdempotencyKey.Trim();
            var existing = await transactions.GetByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing is not null)
            {
                if (databaseTransaction is not null)
                {
                    await databaseTransaction.CommitAsync(cancellationToken);
                }

                return new CreateTransactionResult(existing, true);
            }

            var normalizedCurrency = NormalizeCurrency(command.Currency);
            await EnsureAccountAsync(command.FromAccount, normalizedCurrency, cancellationToken);
            await EnsureAccountAsync(command.ToAccount, normalizedCurrency, cancellationToken);

            var transaction = Transaction.Create(
                command.FromAccount,
                command.ToAccount,
                command.Amount,
                normalizedCurrency,
                normalizedKey);

            transactions.Add(transaction);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (databaseTransaction is not null)
                {
                    await databaseTransaction.CommitAsync(cancellationToken);
                }

                return new CreateTransactionResult(transaction, false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception) && databaseTransaction is not null)
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                var concurrentTransaction = await transactions.GetByIdempotencyKeyAsync(normalizedKey, cancellationToken);
                if (concurrentTransaction is not null)
                {
                    return new CreateTransactionResult(concurrentTransaction, true);
                }

                throw;
            }
        }
        finally
        {
            if (databaseTransaction is not null)
            {
                await databaseTransaction.DisposeAsync();
            }
        }
    }

    public Task<Transaction?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        transactions.GetByIdAsync(id, cancellationToken);

    private async Task EnsureAccountAsync(string accountId, string currency, CancellationToken cancellationToken)
    {
        var normalizedId = accountId.Trim();
        var account = await db.Accounts.SingleOrDefaultAsync(item => item.Id == normalizedId, cancellationToken);

        if (account is null)
        {
            db.Accounts.Add(new Account(normalizedId, currency));
            return;
        }

        if (!account.IsActive)
        {
            throw new DomainValidationException($"Account '{normalizedId}' is inactive.");
        }

        if (!string.Equals(account.Currency, currency, StringComparison.Ordinal))
        {
            throw new DomainValidationException($"Account '{normalizedId}' uses currency {account.Currency}.");
        }
    }

    private static void ValidateRequest(CreateTransactionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.FromAccount) || string.IsNullOrWhiteSpace(command.ToAccount))
        {
            throw new DomainValidationException("Both accounts are required.");
        }

        if (command.Amount <= 0)
        {
            throw new DomainValidationException("Amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(command.Currency) || command.Currency.Trim().Length != 3)
        {
            throw new DomainValidationException("Currency must be a 3-letter code.");
        }

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            throw new DomainValidationException("Idempotency-Key is required.");
        }
    }

    private static string NormalizeCurrency(string currency) => currency.Trim().ToUpperInvariant();

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
