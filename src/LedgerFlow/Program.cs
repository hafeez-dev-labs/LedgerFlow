using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TransactionStore>();

var app = builder.Build();

app.MapPost("/transactions", (CreateTransactionRequest request, HttpRequest httpRequest, TransactionStore store) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    }

    if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.FromAccount) || string.IsNullOrWhiteSpace(request.ToAccount))
    {
        return Results.BadRequest(new { error = "Amount must be positive and both accounts are required." });
    }

    if (request.FromAccount == request.ToAccount)
    {
        return Results.BadRequest(new { error = "Source and destination accounts must differ." });
    }

    if (store.TryGetByIdempotencyKey(idempotencyKey, out var existing))
    {
        return Results.Ok(existing);
    }

    var transaction = new Transaction(
        Guid.NewGuid(),
        request.FromAccount,
        request.ToAccount,
        request.Amount,
        request.Currency,
        TransactionStatus.Completed,
        DateTimeOffset.UtcNow,
        idempotencyKey,
        [
            new LedgerEntry(request.FromAccount, EntryType.Debit, request.Amount, request.Currency),
            new LedgerEntry(request.ToAccount, EntryType.Credit, request.Amount, request.Currency)
        ]);

    store.Add(transaction);
    return Results.Created($"/transactions/{transaction.Id}", transaction);
});

app.MapGet("/transactions/{id:guid}", (Guid id, TransactionStore store) =>
    store.TryGet(id, out var transaction)
        ? Results.Ok(transaction)
        : Results.NotFound());

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;

public record CreateTransactionRequest(string FromAccount, string ToAccount, decimal Amount, string Currency = "USD");

public record Transaction(
    Guid Id,
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency,
    TransactionStatus Status,
    DateTimeOffset CreatedAt,
    string IdempotencyKey,
    IReadOnlyList<LedgerEntry> LedgerEntries);

public record LedgerEntry(string AccountId, EntryType Type, decimal Amount, string Currency);

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

public sealed class TransactionStore
{
    private readonly ConcurrentDictionary<Guid, Transaction> transactions = new();
    private readonly ConcurrentDictionary<string, Guid> idempotencyKeys = new(StringComparer.Ordinal);

    public void Add(Transaction transaction)
    {
        if (!idempotencyKeys.TryAdd(transaction.IdempotencyKey, transaction.Id))
        {
            return;
        }

        transactions[transaction.Id] = transaction;
    }

    public bool TryGet(Guid id, out Transaction? transaction) => transactions.TryGetValue(id, out transaction);

    public bool TryGetByIdempotencyKey(string key, out Transaction? transaction)
    {
        transaction = null;
        return idempotencyKeys.TryGetValue(key, out var id) && transactions.TryGetValue(id, out transaction);
    }
}
