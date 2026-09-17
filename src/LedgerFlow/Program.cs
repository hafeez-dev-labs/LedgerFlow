using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var isTesting = builder.Environment.IsEnvironment("Testing");

builder.Services.AddDbContext<LedgerFlowDbContext>(options =>
{
    if (isTesting)
    {
        options.UseInMemoryDatabase("LedgerFlowTests");
        return;
    }

    var connectionString = builder.Configuration.GetConnectionString("LedgerFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:LedgerFlow is required.");
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<RetryService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<OutboxPublisher>();
builder.Services.AddScoped<IEventConsumer, TransactionEventConsumer>();
builder.Services.AddSingleton<IEventBroker, InMemoryEventBroker>();

if (!isTesting)
{
    builder.Services.AddHostedService<OutboxPublisherWorker>();
    builder.Services.AddHostedService<EventConsumerWorker>();
    builder.Services.AddHostedService<RetryWorker>();
}

var app = builder.Build();

if (!isTesting)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
    db.Database.Migrate();
}

app.MapPost("/transactions", async (CreateTransactionRequest request, HttpRequest httpRequest, TransactionService service, CancellationToken cancellationToken) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    try
    {
        var result = await service.CreateAsync(new CreateTransactionCommand(request.FromAccount, request.ToAccount, request.Amount, request.Currency, idempotencyKey), cancellationToken);
        return result.AlreadyExisted ? Results.Ok(result.Transaction) : Results.Created($"/transactions/{result.Transaction.Id}", result.Transaction);
    }
    catch (DomainValidationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapPost("/transactions/{id:guid}/transitions", async (Guid id, TransitionTransactionRequest request, TransactionService service, RetryService retryService, LedgerFlowDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        var current = await service.GetAsync(id, cancellationToken);
        if (current?.Status == TransactionStatus.Failed && request.Status == TransactionStatus.Processing)
            throw new DomainValidationException($"Transaction '{id}' must use the retry operation for recovery.");
        var transaction = await service.TransitionAsync(id, request.Status, request.FailureReason, cancellationToken);
        if (request.Status == TransactionStatus.Failed) await retryService.RecordFailureAsync(transaction, request.FailureReason!, cancellationToken);
        if (request.Status == TransactionStatus.Completed) await retryService.MarkRecoveredAsync(id, cancellationToken);
        return Results.Ok(transaction);
    }
    catch (DomainValidationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapPost("/transactions/{id:guid}/retry", async (Guid id, RetryService retryService, CancellationToken cancellationToken) =>
{
    try
    {
        var transaction = await retryService.RetryNowAsync(id, cancellationToken);
        return Results.Ok(transaction);
    }
    catch (DomainValidationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/transactions/{id:guid}/recovery", async (Guid id, RetryService retryService, CancellationToken cancellationToken) =>
{
    var status = await retryService.GetStatusAsync(id, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapGet("/transactions/{id:guid}", async (Guid id, TransactionService service, CancellationToken cancellationToken) =>
{
    var transaction = await service.GetAsync(id, cancellationToken);
    return transaction is null ? Results.NotFound() : Results.Ok(transaction);
});

app.MapPost("/reconciliation", async (ReconciliationRequest request, HttpRequest httpRequest, ReconciliationService service, CancellationToken cancellationToken) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    try
    {
        var run = await service.ReconcileAsync(request.Records, idempotencyKey, cancellationToken);
        return Results.Ok(run);
    }
    catch (DomainValidationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/reconciliation/{id:guid}", async (Guid id, ReconciliationService service, CancellationToken cancellationToken) =>
{
    var run = await service.GetAsync(id, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.Run();

public partial class Program;

public sealed record CreateTransactionRequest(string FromAccount, string ToAccount, decimal Amount, string Currency = "USD");
public sealed record TransitionTransactionRequest(TransactionStatus Status, string? FailureReason = null);
public sealed record ReconciliationRequest(IReadOnlyList<ExternalSettlementRecord> Records);
