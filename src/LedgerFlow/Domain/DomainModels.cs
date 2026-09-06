namespace LedgerFlow.Domain;

public enum TransactionStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum EntryType
{
    Debit,
    Credit
}

public sealed class DomainValidationException(string message) : Exception(message);

public sealed class Account
{
    private Account() { }

    public Account(string id, string currency)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new DomainValidationException("Account id is required.");
        }

        Currency = NormalizeCurrency(currency);
        Id = id.Trim();
        IsActive = true;
    }

    public string Id { get; private set; } = null!;
    public string Currency { get; private set; } = null!;
    public bool IsActive { get; private set; }

    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            throw new DomainValidationException("Currency must be a 3-letter code.");
        }

        return currency.Trim().ToUpperInvariant();
    }
}

public sealed class LedgerEntry
{
    private LedgerEntry() { }

    internal LedgerEntry(Guid transactionId, string accountId, EntryType type, decimal amount, string currency)
    {
        if (amount <= 0)
        {
            throw new DomainValidationException("Ledger entry amount must be positive.");
        }

        Id = Guid.NewGuid();
        TransactionId = transactionId;
        AccountId = accountId;
        Type = type;
        Amount = decimal.Round(amount, 4, MidpointRounding.ToEven);
        Currency = currency;
        PostedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public string AccountId { get; private set; } = null!;
    public EntryType Type { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset PostedAt { get; private set; }
}

public sealed class Transaction
{
    private Transaction() { }

    private Transaction(
        Guid id,
        string fromAccount,
        string toAccount,
        decimal amount,
        string currency,
        string idempotencyKey)
    {
        Id = id;
        FromAccountId = fromAccount;
        ToAccountId = toAccount;
        Amount = decimal.Round(amount, 4, MidpointRounding.ToEven);
        Currency = NormalizeCurrency(currency);
        Status = TransactionStatus.Completed;
        CreatedAt = DateTimeOffset.UtcNow;
        IdempotencyKey = idempotencyKey;
    }

    public Guid Id { get; private set; }
    public string FromAccountId { get; private set; } = null!;
    public string ToAccountId { get; private set; } = null!;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public TransactionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public List<LedgerEntry> LedgerEntries { get; private set; } = [];

    public static Transaction Create(
        string fromAccount,
        string toAccount,
        decimal amount,
        string currency,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(fromAccount) || string.IsNullOrWhiteSpace(toAccount))
        {
            throw new DomainValidationException("Both accounts are required.");
        }

        if (string.Equals(fromAccount.Trim(), toAccount.Trim(), StringComparison.Ordinal))
        {
            throw new DomainValidationException("Source and destination accounts must differ.");
        }

        if (amount <= 0)
        {
            throw new DomainValidationException("Amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainValidationException("Idempotency-Key is required.");
        }

        var normalizedFrom = fromAccount.Trim();
        var normalizedTo = toAccount.Trim();
        var normalizedKey = idempotencyKey.Trim();
        var normalizedCurrency = NormalizeCurrency(currency);
        var transaction = new Transaction(Guid.NewGuid(), normalizedFrom, normalizedTo, amount, normalizedCurrency, normalizedKey);

        transaction.LedgerEntries.Add(new LedgerEntry(transaction.Id, normalizedFrom, EntryType.Debit, transaction.Amount, transaction.Currency));
        transaction.LedgerEntries.Add(new LedgerEntry(transaction.Id, normalizedTo, EntryType.Credit, transaction.Amount, transaction.Currency));

        transaction.EnsureBalanced();
        return transaction;
    }

    private void EnsureBalanced()
    {
        var debitTotal = LedgerEntries.Where(entry => entry.Type == EntryType.Debit).Sum(entry => entry.Amount);
        var creditTotal = LedgerEntries.Where(entry => entry.Type == EntryType.Credit).Sum(entry => entry.Amount);

        if (debitTotal != creditTotal || debitTotal <= 0)
        {
            throw new DomainValidationException("Ledger entries must balance.");
        }
    }

    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            throw new DomainValidationException("Currency must be a 3-letter code.");
        }

        return currency.Trim().ToUpperInvariant();
    }
}
