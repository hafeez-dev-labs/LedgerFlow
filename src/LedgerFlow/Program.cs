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

var app = builder.Build();

if (!isTesting)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LedgerFlowDbContext>();
    db.Database.Migrate();
}

app.MapPost("/transactions", async (
    CreateTransactionRequest request,
    HttpRequest httpRequest,
    TransactionService service,
    CancellationToken cancellationToken) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    }

    try
    {
        var result = await service.CreateAsync(
            new CreateTransactionCommand(
                request.FromAccount,
                request.ToAccount,
                request.Amount,
                request.Currency,
                idempotencyKey),
            cancellationToken);

        return result.AlreadyExisted
            ? Results.Ok(result.Transaction)
            : Results.Created($"/transactions/{result.Transaction.Id}", result.Transaction);
    }
    catch (DomainValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/transactions/{id:guid}", async (
    Guid id,
    TransactionService service,
    CancellationToken cancellationToken) =>
{
    var transaction = await service.GetAsync(id, cancellationToken);
    return transaction is null ? Results.NotFound() : Results.Ok(transaction);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;

public sealed record CreateTransactionRequest(
    string FromAccount,
    string ToAccount,
    decimal Amount,
    string Currency = "USD");
