using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace LedgerFlow.Tests;

public sealed class PostgreSqlIdempotencyTests
{
    [Fact]
    public async Task ConcurrentRequestsAcrossIndependentContexts_CreateOneFinancialEffect()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("LEDGERFLOW_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            throw new SkipException("Set LEDGERFLOW_TEST_CONNECTION_STRING to a dedicated PostgreSQL test database to run this integration test.");

        var connectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        if (string.IsNullOrWhiteSpace(connectionString.Database) ||
            !connectionString.Database.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The PostgreSQL integration test database name must end with '_test'.");
        }

        var schema = $"ledgerflow_test_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(configuredConnectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = adminConnection.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await createSchema.ExecuteNonQueryAsync();
        }

        connectionString.SearchPath = schema;
        var options = new DbContextOptionsBuilder<LedgerFlowDbContext>()
            .UseNpgsql(connectionString.ConnectionString)
            .Options;

        try
        {
            await using (var setupDb = new LedgerFlowDbContext(options))
            {
                await setupDb.Database.MigrateAsync();
                setupDb.Accounts.AddRange(
                    new Account("customer-concurrent", "USD"),
                    new Account("merchant-concurrent", "USD"));
                await setupDb.SaveChangesAsync();
            }

            var key = Guid.NewGuid().ToString("N");
            var command = new CreateTransactionCommand(
                "customer-concurrent",
                "merchant-concurrent",
                125.50m,
                "USD",
                key);

            var results = await Task.WhenAll(
                CreateInIndependentContext(options, command),
                CreateInIndependentContext(options, command));

            Assert.Single(results, result => !result.AlreadyExisted);
            Assert.Single(results, result => result.AlreadyExisted);
            Assert.Equal(results[0].Transaction.Id, results[1].Transaction.Id);

            await using (var assertionDb = new LedgerFlowDbContext(options))
            {
                var transactionId = results[0].Transaction.Id;
                Assert.Equal(1, await assertionDb.Transactions.CountAsync(item => item.IdempotencyKey == key));

                var entries = await assertionDb.LedgerEntries
                    .Where(entry => entry.TransactionId == transactionId)
                    .ToListAsync();
                Assert.Equal(2, entries.Count);
                Assert.Single(entries, entry => entry.Type == EntryType.Debit);
                Assert.Single(entries, entry => entry.Type == EntryType.Credit);
                Assert.Equal(0m, entries.Sum(entry => entry.Type == EntryType.Debit ? entry.Amount : -entry.Amount));

                Assert.Equal(1, await assertionDb.OutboxMessages.CountAsync(message => message.AggregateId == transactionId));
            }

            await using (var retryDb = new LedgerFlowDbContext(options))
            {
                var retry = await new TransactionService(retryDb, new TransactionRepository(retryDb))
                    .CreateAsync(command, CancellationToken.None);
                Assert.True(retry.AlreadyExisted);
                Assert.Equal(results[0].Transaction.Id, retry.Transaction.Id);
            }

            await using (var mismatchDb = new LedgerFlowDbContext(options))
            {
                var differentPayload = command with { Amount = 999m };
                var service = new TransactionService(mismatchDb, new TransactionRepository(mismatchDb));
                await Assert.ThrowsAsync<DomainValidationException>(() =>
                    service.CreateAsync(differentPayload, CancellationToken.None));
            }
        }
        finally
        {
            await using var dropSchema = adminConnection.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task<CreateTransactionResult> CreateInIndependentContext(
        DbContextOptions<LedgerFlowDbContext> options,
        CreateTransactionCommand command)
    {
        await using var db = new LedgerFlowDbContext(options);
        var service = new TransactionService(db, new TransactionRepository(db));
        return await service.CreateAsync(command, CancellationToken.None);
    }
}
