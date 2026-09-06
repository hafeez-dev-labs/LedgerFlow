using LedgerFlow.Domain;
using Microsoft.EntityFrameworkCore;

namespace LedgerFlow.Infrastructure;

public sealed class LedgerFlowDbContext(DbContextOptions<LedgerFlowDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

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
    }
}
