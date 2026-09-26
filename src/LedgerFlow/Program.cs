using System.Diagnostics;
using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var isTesting = builder.Environment.IsEnvironment("Testing");
var otlpEndpointValue = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var hasOtlpEndpoint = Uri.TryCreate(otlpEndpointValue, UriKind.Absolute, out var otlpEndpoint);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: LedgerFlowTelemetry.ServiceName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(LedgerFlowTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation(options => options.RecordException = true);

        if (hasOtlpEndpoint)
            tracing.AddOtlpExporter(options => options.Endpoint = otlpEndpoint!);
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(LedgerFlowTelemetry.MeterName)
            .AddAspNetCoreInstrumentation();

        if (hasOtlpEndpoint)
            metrics.AddOtlpExporter(options => options.Endpoint = otlpEndpoint!);
    });

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
    logging.ParseStateValues = true;

    if (hasOtlpEndpoint)
        logging.AddOtlpExporter(options => options.Endpoint = otlpEndpoint!);
});

builder.Services.AddDbContextFactory<LedgerFlowDbContext>(options =>
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
builder.Services.AddSingleton<IAuditTrailWriter, AuditTrailWriter>();
builder.Services.AddSingleton<SettlementService>();
builder.Services.AddScoped<IEventConsumer, TransactionEventConsumer>();
builder.Services.AddSingleton<IEventBroker, InMemoryEventBroker>();
builder.Services.AddSingleton(new FraudRuleOptions(
    ReadDecimal("FRAUD_THRESHOLD", 10000m),
    ReadInt("FRAUD_VELOCITY_LIMIT", 5),
    ReadSet("FRAUD_RESTRICTED_ACCOUNTS")));
builder.Services.AddSingleton<FraudRuleService>();

if (!isTesting)
{
    builder.Services.AddHostedService<OutboxPublisherWorker>();
    builder.Services.AddHostedService<EventConsumerWorker>();
    builder.Services.AddHostedService<RetryWorker>();
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LedgerFlow.Request");
    var correlationId = context.Request.Headers[LedgerFlowTelemetry.CorrelationHeader].FirstOrDefault()
        ?? Activity.Current?.TraceId.ToString()
        ?? Guid.NewGuid().ToString("N");

    Activity.Current?.SetTag("correlation.id", correlationId);
    context.Response.Headers[LedgerFlowTelemetry.CorrelationHeader] = correlationId;

    var startedAt = Stopwatch.GetTimestamp();
    using (logger.BeginScope(new Dictionary<string, object?>
    {
        ["CorrelationId"] = correlationId,
        ["Method"] = context.Request.Method,
        ["Path"] = context.Request.Path.ToString()
    }))
    {
        try
        {
            await next();
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            logger.LogInformation(
                "HTTP request completed with status {StatusCode} in {DurationMs} ms.",
                context.Response.StatusCode,
                elapsed.TotalMilliseconds);
        }
    }
});

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

app.MapGet("/audit/transactions/{transactionId:guid}", (Guid transactionId, IAuditTrailWriter auditTrail) =>
    Results.Ok(auditTrail.GetByTransaction(transactionId)));

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

app.MapPost("/fraud/evaluate", (FraudEvaluationRequest request, FraudRuleService service) =>
{
    try
    {
        return Results.Ok(service.Evaluate(request));
    }
    catch (DomainValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/fraud/decisions/{transactionId:guid}", (Guid transactionId, FraudRuleService service) =>
    Results.Ok(service.GetDecisions(transactionId)));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.Run();

static decimal ReadDecimal(string name, decimal fallback) =>
    decimal.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

static int ReadInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

static IReadOnlySet<string> ReadSet(string name) =>
    (Environment.GetEnvironmentVariable(name) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

public partial class Program;

public sealed record CreateTransactionRequest(string FromAccount, string ToAccount, decimal Amount, string Currency = "USD");
public sealed record TransitionTransactionRequest(TransactionStatus Status, string? FailureReason = null);
public sealed record ReconciliationRequest(IReadOnlyList<ExternalSettlementRecord> Records);
