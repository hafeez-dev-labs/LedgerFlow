using LedgerFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Infrastructure;

public sealed class LedgerFlowDbContext(DbContextOptions<LedgerFlowDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<TransactionStateTransition> TransactionStateTransitions => Set<TransactionStateTransition>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<RetrySchedule> RetrySchedules => Set<RetrySchedule>();
    public DbSet<DeadLetterRecord> DeadLetterRecords => Set<DeadLetterRecord>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationResult> ReconciliationResults => Set<ReconciliationResult>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Id).HasMaxLength(100);
            entity.Property(account => account.Currency).HasColumnType("varchar(3)").IsRequired();
            entity.Property(account => account.IsActive).IsRequired();
            entity.HasCheckConstraint("ck_accounts_currency_length", "char_length(currency) = 3");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.FromAccountId).HasMaxLength(100).IsRequired();
            entity.Property(transaction => transaction.ToAccountId).HasMaxLength(100).IsRequired();
            entity.Property(transaction => transaction.Amount).HasPrecision(19, 4).IsRequired();
            entity.Property(transaction => transaction.Currency).HasColumnType("varchar(3)").IsRequired();
            entity.Property(transaction => transaction.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(transaction => transaction.CreatedAt).IsRequired();
            entity.Property(transaction => transaction.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(transaction => transaction.IdempotencyKey).IsUnique();
            entity.HasOne<Account>().WithMany().HasForeignKey(transaction => transaction.FromAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Account>().WithMany().HasForeignKey(transaction => transaction.ToAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(transaction => transaction.LedgerEntries).WithOne().HasForeignKey(entry => entry.TransactionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(transaction => transaction.StateTransitions).WithOne().HasForeignKey(transition => transition.TransactionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasCheckConstraint("ck_transactions_amount_positive", "amount > 0");
            entity.HasCheckConstraint("ck_transactions_distinct_accounts", "from_account_id <> to_account_id");
            entity.HasCheckConstraint("ck_transactions_currency_length", "char_length(currency) = 3");
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("journal_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.AccountId).HasMaxLength(100).IsRequired();
            entity.Property(entry => entry.Type).HasConversion<string>().HasMaxLength(10).IsRequired();
            entity.Property(entry => entry.Amount).HasPrecision(19, 4).IsRequired();
            entity.Property(entry => entry.Currency).HasColumnType("varchar(3)").IsRequired();
            entity.Property(entry => entry.PostedAt).IsRequired();
            entity.HasOne<Account>().WithMany().HasForeignKey(entry => entry.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasCheckConstraint("ck_journal_entries_amount_positive", "amount > 0");
            entity.HasCheckConstraint("ck_journal_entries_currency_length", "char_length(currency) = 3");
        });

        modelBuilder.Entity<TransactionStateTransition>(entity =>
        {
            entity.ToTable("transaction_state_transitions");
            entity.HasKey(transition => transition.Id);
            entity.Property(transition => transition.FromStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(transition => transition.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(transition => transition.TransitionedAt).IsRequired();
            entity.Property(transition => transition.FailureReason).HasMaxLength(1000);
            entity.HasIndex(transition => new { transition.TransactionId, transition.TransitionedAt });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(200).IsRequired();
            entity.Property(message => message.AggregateId).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
            entity.Property(message => message.OccurredAt).IsRequired();
            entity.Property(message => message.PublishedAt);
            entity.HasIndex(message => new { message.PublishedAt, message.OccurredAt });
        });

        modelBuilder.Entity<ProcessedEvent>(entity =>
        {
            entity.ToTable("processed_events");
            entity.HasKey(eventRecord => eventRecord.Id);
            entity.Property(eventRecord => eventRecord.EventType).HasMaxLength(200).IsRequired();
            entity.Property(eventRecord => eventRecord.AggregateId).IsRequired();
            entity.Property(eventRecord => eventRecord.ProcessedAt).IsRequired();
            entity.HasIndex(eventRecord => new { eventRecord.AggregateId, eventRecord.ProcessedAt });
        });

        modelBuilder.Entity<RetrySchedule>(entity =>
        {
            entity.ToTable("retry_schedules");
            entity.HasKey(schedule => schedule.Id);
            entity.Property(schedule => schedule.AttemptCount).IsRequired();
            entity.Property(schedule => schedule.MaxAttempts).IsRequired();
            entity.Property(schedule => schedule.NextAttemptAt);
            entity.Property(schedule => schedule.LastFailureReason).HasMaxLength(1000);
            entity.Property(schedule => schedule.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(schedule => schedule.ResolvedAt);
            entity.HasIndex(schedule => schedule.TransactionId).IsUnique();
            entity.HasOne<Transaction>().WithMany().HasForeignKey(schedule => schedule.TransactionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(schedule => new { schedule.Status, schedule.NextAttemptAt });
        });

        modelBuilder.Entity<DeadLetterRecord>(entity =>
        {
            entity.ToTable("dead_letter_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.RetryAttempts).IsRequired();
            entity.Property(record => record.FailureReason).HasMaxLength(1000).IsRequired();
            entity.Property(record => record.FailedAt).IsRequired();
            entity.Property(record => record.ResolvedAt);
            entity.HasIndex(record => record.TransactionId).IsUnique();
            entity.HasOne<Transaction>().WithMany().HasForeignKey(record => record.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationRun>(entity =>
        {
            entity.ToTable("reconciliation_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(run => run.StartedAt).IsRequired();
            entity.Property(run => run.CompletedAt);
            entity.HasIndex(run => run.IdempotencyKey).IsUnique();
            entity.HasMany(run => run.Results).WithOne().HasForeignKey(result => result.ReconciliationRunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("audit_records");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.EventType).HasMaxLength(100).IsRequired();
            entity.Property(record => record.AggregateType).HasMaxLength(100).IsRequired();
            entity.Property(record => record.AggregateId).IsRequired();
            entity.Property(record => record.TransactionId);
            entity.Property(record => record.CorrelationId).HasMaxLength(200);
            entity.Property(record => record.OccurredAt).IsRequired();
            entity.Property(record => record.Payload).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(record => new { record.TransactionId, record.OccurredAt });
            entity.HasIndex(record => new { record.AggregateType, record.AggregateId, record.OccurredAt });
        });

        modelBuilder.Entity<ReconciliationResult>(entity =>
        {
            entity.ToTable("reconciliation_results");
            entity.HasKey(result => result.Id);
            entity.Property(result => result.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(result => result.ExternalTransactionId).HasMaxLength(200);
            entity.Property(result => result.InternalAmount).HasPrecision(19, 4);
            entity.Property(result => result.ExternalAmount).HasPrecision(19, 4);
            entity.Property(result => result.InternalCurrency).HasColumnType("varchar(3)");
            entity.Property(result => result.ExternalCurrency).HasColumnType("varchar(3)");
            entity.Property(result => result.Explanation).HasMaxLength(1000).IsRequired();
            entity.HasIndex(result => new { result.ReconciliationRunId, result.Status });
        });
    }
}
