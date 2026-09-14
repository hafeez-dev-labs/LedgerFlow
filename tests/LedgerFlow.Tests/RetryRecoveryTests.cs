using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LedgerFlow.Tests;

public class RetryRecoveryTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory factory;

    public RetryRecoveryTests(TestingWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task FailedTransaction_SchedulesBoundedRetryWithBackoff()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var retryService = scope.ServiceProvider.GetRequiredService<RetryService>();
        var transaction = Transaction.Create("retry-source-001", "retry-destination-001", 10m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        transaction.TransitionTo(TransactionStatus.Processing);
        transaction.TransitionTo(TransactionStatus.Failed, "Temporary processor outage");
        await retryService.RecordFailureAsync(transaction, "Temporary processor outage", CancellationToken.None);

        var schedule = await db.RetrySchedules.SingleAsync(item => item.TransactionId == transaction.Id);
        Assert.Equal(RetryStatus.Scheduled, schedule.Status);
        Assert.Equal(1, schedule.AttemptCount);
        Assert.Equal(3, schedule.MaxAttempts);
        Assert.True(schedule.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal("Temporary processor outage", schedule.LastFailureReason);
    }

    [Fact]
    public async Task RetryFailures_ExhaustIntoDeadLetter()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var retryService = scope.ServiceProvider.GetRequiredService<RetryService>();
        var transaction = Transaction.Create("retry-source-002", "retry-destination-002", 20m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await retryService.RecordFailureAsync(transaction, $"Failure {attempt + 1}", CancellationToken.None);
        }

        var schedule = await db.RetrySchedules.SingleAsync(item => item.TransactionId == transaction.Id);
        var deadLetter = await db.DeadLetterRecords.SingleAsync(item => item.TransactionId == transaction.Id);

        Assert.Equal(RetryStatus.DeadLettered, schedule.Status);
        Assert.Equal(3, schedule.AttemptCount);
        Assert.Null(schedule.NextAttemptAt);
        Assert.Equal(3, deadLetter.RetryAttempts);
        Assert.Equal("Failure 3", deadLetter.FailureReason);
    }

    [Fact]
    public async Task CompletedRecovery_MarksRetryScheduleRecovered()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
        var retryService = scope.ServiceProvider.GetRequiredService<RetryService>();
        var transaction = Transaction.Create("retry-source-003", "retry-destination-003", 30m, "USD", Guid.NewGuid().ToString());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        await retryService.RecordFailureAsync(transaction, "Transient dependency failure", CancellationToken.None);
        await retryService.MarkRecoveredAsync(transaction.Id, CancellationToken.None);

        var schedule = await db.RetrySchedules.SingleAsync(item => item.TransactionId == transaction.Id);
        Assert.Equal(RetryStatus.Recovered, schedule.Status);
        Assert.Null(schedule.NextAttemptAt);
        Assert.NotNull(schedule.ResolvedAt);
    }
}
