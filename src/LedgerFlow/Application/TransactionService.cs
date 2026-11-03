using System.Collections.Concurrent;
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
    ITransactionRepository transactions,
    IAuditTrailWriter? auditTrail = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InMemoryIdempotencyLocks = new(StringComparer.Ordinal);

    public async Task<CreateTransactionResult> CreateAsync(CreateTransactionCommand command, CancellationToken cancellationToken)
    {
        using var activity = LedgerFlowTelemetry.StartActivity("transaction.create");
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        ValidateRequest(command);

        var normalizedKey = command.IdempotencyKey.Trim();
        var normalizedCurrency = NormalizeCurrency(command.Currency);
        SemaphoreSlim? inMemoryLock = null;
        if (db.Database.IsInMemory())
        {
            inMemoryLock = InMemoryIdempotencyLocks.GetOrAdd(normalizedKey, _ => new SemaphoreSlim(1, 1));
            await inMemoryLock.WaitAsync(cancellationToken);
        }

        IDbContextTransaction? databaseTransaction = null;
        if (!db.Database.IsInMemory())
            databaseTransaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var existing = await transactions.GetByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing is not null)
            {
                EnsureSameRequest(existing, command, normalizedCurrency);
                if (databaseTransaction is not null)
                    await databaseTransaction.CommitAsync(cancellationToken);

                LedgerFlowTelemetry.Add(LedgerFlowTelemetry.Transactions, "outcome", "idempotent");
                LedgerFlowTelemetry.Record(LedgerFlowTelemetry.ProcessingDuration, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, "operation", "transaction.create");
                return new CreateTransactionResult(existing, true);
            }

            await EnsureAccountAsync(command.FromAccount, normalizedCurrency, cancellationToken);
            await EnsureAccountAsync(command.ToAccount, normalizedCurrency, cancellationToken);

            var transaction = Transaction.Create(
                command.FromAccount,
                command.ToAccount,
                command.Amount,
                normalizedCurrency,
                normalizedKey);

            transactions.Add(transaction);
            AddEvent(transaction, transaction.Status, null);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (databaseTransaction is not null)
                    await databaseTransaction.CommitAsync(cancellationToken);

                LedgerFlowTelemetry.Add(LedgerFlowTelemetry.Transactions, "outcome", "created");
                LedgerFlowTelemetry.Record(LedgerFlowTelemetry.ProcessingDuration, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, "operation", "transaction.create");
                activity?.SetTag("ledgerflow.transaction_id", transaction.Id);

                auditTrail?.Record(
                    "transaction.created",
                    "transaction",
                    transaction.Id,
                    transaction.Id,
                    normalizedKey,
                    new { transaction.Amount, transaction.Currency, transaction.FromAccountId, transaction.ToAccountId });
                auditTrail?.Record(
                    "ledger.posted",
                    "ledger",
                    transaction.Id,
                    transaction.Id,
                    normalizedKey,
                    new { entries = transaction.LedgerEntries.Select(entry => new { entry.AccountId, entry.Type, entry.Amount, entry.Currency }).ToArray() });

                return new CreateTransactionResult(transaction, false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception) && databaseTransaction is not null)
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                var concurrentTransaction = await transactions.GetByIdempotencyKeyAsync(normalizedKey, cancellationToken);
                if (concurrentTransaction is not null)
                {
                    EnsureSameRequest(concurrentTransaction, command, normalizedCurrency);
                    return new CreateTransactionResult(concurrentTransaction, true);
                }

                throw;
            }
        }
        finally
        {
            if (databaseTransaction is not null)
                await databaseTransaction.DisposeAsync();
            if (inMemoryLock is not null)
            {
                inMemoryLock.Release();
                if (inMemoryLock.CurrentCount == 1)
                    InMemoryIdempotencyLocks.TryRemove(normalizedKey, out _);
            }
        }
    }

    public async Task<Transaction?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await transactions.GetByIdAsync(id, cancellationToken);

    public async Task<Transaction> TransitionAsync(
        Guid id,
        TransactionStatus targetStatus,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        using var activity = LedgerFlowTelemetry.StartActivity("transaction.transition", id);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        IDbContextTransaction? databaseTransaction = null;
        if (!db.Database.IsInMemory())
            databaseTransaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var transaction = await transactions.GetByIdAsync(id, cancellationToken)
                ?? throw new DomainValidationException($"Transaction '{id}' was not found.");

            transaction.TransitionTo(targetStatus, failureReason);
            AddEvent(transaction, targetStatus, failureReason);
            await db.SaveChangesAsync(cancellationToken);

            if (databaseTransaction is not null)
                await databaseTransaction.CommitAsync(cancellationToken);

            var transition = transaction.StateTransitions[^1];
            LedgerFlowTelemetry.Add(
                LedgerFlowTelemetry.Transactions,
                "outcome",
                targetStatus == TransactionStatus.Completed ? "completed" : targetStatus == TransactionStatus.Failed ? "failed" : "transitioned");
            LedgerFlowTelemetry.Record(
                LedgerFlowTelemetry.ProcessingDuration,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                "operation",
                "transaction.transition");

            auditTrail?.Record(
                "transaction.transitioned",
                "transaction",
                transaction.Id,
                transaction.Id,
                transaction.Id.ToString(),
                new { from = transition.FromStatus, to = transition.ToStatus, transition.FailureReason, transition.TransitionedAt });

            return transaction;
        }
        finally
        {
            if (databaseTransaction is not null)
                await databaseTransaction.DisposeAsync();
        }
    }

    private static void EnsureSameRequest(Transaction existing, CreateTransactionCommand command, string normalizedCurrency)
    {
        var sameRequest =
            string.Equals(existing.FromAccountId, command.FromAccount.Trim(), StringComparison.Ordinal) &&
            string.Equals(existing.ToAccountId, command.ToAccount.Trim(), StringComparison.Ordinal) &&
            existing.Amount == decimal.Round(command.Amount, 4, MidpointRounding.ToEven) &&
            string.Equals(existing.Currency, normalizedCurrency, StringComparison.Ordinal);

        if (!sameRequest)
            throw new DomainValidationException("Idempotency-Key has already been used with a different transaction request.");
    }

    private void AddEvent(Transaction transaction, TransactionStatus status, string? failureReason)
    {
        var transactionEvent = new TransactionEvent(
            Guid.NewGuid(),
            transaction.Id,
            status,
            DateTimeOffset.UtcNow,
            failureReason);
        db.OutboxMessages.Add(new OutboxMessage(transactionEvent));
    }

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
            throw new DomainValidationException($"Account '{normalizedId}' is inactive.");
        if (!string.Equals(account.Currency, currency, StringComparison.Ordinal))
            throw new DomainValidationException($"Account '{normalizedId}' uses currency {account.Currency}.");
    }

    private static void ValidateRequest(CreateTransactionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.FromAccount) || string.IsNullOrWhiteSpace(command.ToAccount))
            throw new DomainValidationException("Both accounts are required.");
        if (command.Amount <= 0)
            throw new DomainValidationException("Amount must be positive.");
        if (string.IsNullOrWhiteSpace(command.Currency) || command.Currency.Trim().Length != 3)
            throw new DomainValidationException("Currency must be a 3-letter code.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new DomainValidationException("Idempotency-Key is required.");
    }

    private static string NormalizeCurrency(string currency) => currency.Trim().ToUpperInvariant();

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
