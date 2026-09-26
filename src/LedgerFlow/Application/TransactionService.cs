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
    public async Task<CreateTransactionResult> CreateAsync(CreateTransactionCommand command, CancellationToken cancellationToken)
    {
        using var activity = LedgerFlowTelemetry.StartActivity("transaction.create");
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
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

                LedgerFlowTelemetry.Add(LedgerFlowTelemetry.Transactions, "outcome", "idempotent");
                LedgerFlowTelemetry.Record(LedgerFlowTelemetry.ProcessingDuration, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, "operation", "transaction.create");
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
            AddEvent(transaction, transaction.Status, null);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (databaseTransaction is not null)
                {
                    await databaseTransaction.CommitAsync(cancellationToken);
                }

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
        {
            databaseTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var transaction = await transactions.GetByIdAsync(id, cancellationToken)
                ?? throw new DomainValidationException($"Transaction '{id}' was not found.");

            transaction.TransitionTo(targetStatus, failureReason);
            AddEvent(transaction, targetStatus, failureReason);
            await db.SaveChangesAsync(cancellationToken);

            if (databaseTransaction is not null)
            {
                await databaseTransaction.CommitAsync(cancellationToken);
            }

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
            {
                await databaseTransaction.DisposeAsync();
            }
        }
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
