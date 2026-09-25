using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Application;

public sealed record RecoveryStatus(
    RetryStatus Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastFailureReason,
    DateTimeOffset? ResolvedAt);

public sealed class RetryService(LedgerFlowDbContext db, IAuditTrailWriter? auditTrail = null)
{
    private const int MaxAttempts = 3;

    public async Task RecordFailureAsync(Transaction transaction, string failureReason, CancellationToken cancellationToken)
    {
        var normalizedReason = failureReason.Trim();
        var schedule = await db.RetrySchedules.SingleOrDefaultAsync(item => item.TransactionId == transaction.Id, cancellationToken);
        if (schedule is null)
        {
            schedule = new RetrySchedule(transaction.Id, MaxAttempts, DateTimeOffset.UtcNow);
            db.RetrySchedules.Add(schedule);
        }

        var nextAttemptNumber = schedule.AttemptCount + 1;
        var nextAttemptAt = nextAttemptNumber >= schedule.MaxAttempts
            ? null
            : DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, nextAttemptNumber - 1));
        schedule.RecordFailure(nextAttemptNumber, normalizedReason, nextAttemptAt);

        if (!nextAttemptAt.HasValue)
        {
            var existingDeadLetter = await db.DeadLetterRecords.SingleOrDefaultAsync(item => item.TransactionId == transaction.Id, cancellationToken);
            if (existingDeadLetter is null)
                db.DeadLetterRecords.Add(new DeadLetterRecord(transaction.Id, nextAttemptNumber, normalizedReason, DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync(cancellationToken);
        auditTrail?.Record("retry.recorded", "transaction", transaction.Id, transaction.Id, transaction.Id.ToString(), new { attempt = nextAttemptNumber, nextAttemptAt, reason = normalizedReason, deadLetter = !nextAttemptAt.HasValue });
    }

    public async Task MarkRecoveredAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var schedule = await db.RetrySchedules.SingleOrDefaultAsync(item => item.TransactionId == transactionId, cancellationToken);
        if (schedule is null || schedule.Status == RetryStatus.Recovered) return;

        schedule.MarkRecovered(DateTimeOffset.UtcNow);
        var deadLetter = await db.DeadLetterRecords.SingleOrDefaultAsync(item => item.TransactionId == transactionId && item.ResolvedAt == null, cancellationToken);
        deadLetter?.MarkResolved(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        auditTrail?.Record("retry.recovered", "transaction", transactionId, transactionId, transactionId.ToString(), new { mode = "mark-recovered" });
    }

    public async Task<Transaction> RetryNowAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions
            .Include(item => item.LedgerEntries)
            .Include(item => item.StateTransitions)
            .SingleOrDefaultAsync(item => item.Id == transactionId, cancellationToken)
            ?? throw new DomainValidationException($"Transaction '{transactionId}' was not found.");

        var schedule = await db.RetrySchedules.SingleOrDefaultAsync(item => item.TransactionId == transactionId, cancellationToken)
            ?? throw new DomainValidationException($"Transaction '{transactionId}' has no scheduled recovery.");

        if (schedule.Status != RetryStatus.Scheduled)
            throw new DomainValidationException($"Transaction '{transactionId}' is not eligible for retry.");
        if (schedule.NextAttemptAt > DateTimeOffset.UtcNow)
            throw new DomainValidationException($"Transaction '{transactionId}' is not ready for retry until {schedule.NextAttemptAt:O}.");

        transaction.TransitionTo(TransactionStatus.Processing);
        schedule.MarkRecovered(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        auditTrail?.Record("retry.recovered", "transaction", transactionId, transactionId, transactionId.ToString(), new { mode = "retry-now" });
        return transaction;
    }

    public async Task<int> ProcessDueRetriesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var schedules = await db.RetrySchedules
            .Where(item => item.Status == RetryStatus.Scheduled && item.NextAttemptAt <= now)
            .OrderBy(item => item.NextAttemptAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var schedule in schedules)
        {
            var transaction = await db.Transactions
                .Include(item => item.LedgerEntries)
                .Include(item => item.StateTransitions)
                .SingleOrDefaultAsync(item => item.Id == schedule.TransactionId, cancellationToken);

            if (transaction is null || transaction.Status != TransactionStatus.Failed) continue;

            transaction.TransitionTo(TransactionStatus.Processing);
            schedule.MarkRecovered(now);
            processed++;
        }

        if (processed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return processed;
    }

    public async Task<RecoveryStatus?> GetStatusAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var schedule = await db.RetrySchedules
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.TransactionId == transactionId, cancellationToken);

        return schedule is null
            ? null
            : new RecoveryStatus(schedule.Status, schedule.AttemptCount, schedule.MaxAttempts, schedule.NextAttemptAt, schedule.LastFailureReason, schedule.ResolvedAt);
    }
}

public sealed class RetryWorker(IServiceScopeFactory scopeFactory, ILogger<RetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RetryService>().ProcessDueRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process due transaction retries.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
