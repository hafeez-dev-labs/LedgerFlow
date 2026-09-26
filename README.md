# LedgerFlow

A financial transaction processing and reconciliation engine built incrementally to explore financial correctness, distributed systems, reliability, and backend architecture.

LedgerFlow is an engineering simulation rather than a production payment processor. The project models the core problems found in financial transaction platforms while keeping the system testable, observable, and suitable for experimentation.

## Architecture

```text
Client
  |
  v
Transaction API
  |
  v
Validation
  |
  v
Idempotency
  |
  v
Transaction State Machine
  |
  v
Transaction Processing
  |
  +--------------------+
  |                    |
  v                    v
Ledger              Events
  |                    |
  |                    v
  |              Retry / Recovery
  |                    |
  v                    v
Reconciliation <--- Processing Results
  |
  v
Settlement
  |
  v
Audit Trail
  |
  v
Observability
```

## Current Implementation

### Phases 1–5 — Transaction Processing Foundation, Persistent Ledger, State Machine, Events/Outbox, and Retry Recovery ✅

The first five phases are complete and merged. They establish the transaction API, validation, idempotency, balanced double-entry ledger creation, PostgreSQL persistence, explicit transaction state transitions, transactional outbox processing, retry scheduling, bounded backoff, and dead-letter recovery.

### Phase 6 — Reconciliation Engine ✅

Phase 6 adds a durable reconciliation engine that compares completed internal transactions against an external settlement/input source.

Current reconciliation capabilities include:

- Deterministic matching by internal transaction identifier
- Matched transaction results
- Missing internal transaction detection
- Missing external transaction detection
- Amount mismatch detection
- Currency mismatch detection
- Duplicate external record detection
- Persisted reconciliation runs and results
- Idempotent reconciliation requests through `Idempotency-Key`
- Explicit discrepancy explanations
- Automated coverage for matched and mismatched scenarios

Current API surface includes:

```text
POST /transactions
GET  /transactions/{id}
POST /transactions/{id}/transitions
POST /transactions/{id}/retry
GET  /transactions/{id}/recovery
POST /reconciliation
GET  /reconciliation/{id}
GET  /health
```

Example reconciliation request:

```http
POST /reconciliation
Idempotency-Key: reconciliation-2026-09-17
Content-Type: application/json

{
  "records": [
    {
      "externalTransactionId": "settlement-10001",
      "transactionId": "00000000-0000-0000-0000-000000000000",
      "amount": 100.50,
      "currency": "USD"
    }
  ]
}
```

A reconciliation run produces explicit results such as `Matched`, `MissingInternal`, `MissingExternal`, `AmountMismatch`, `CurrencyMismatch`, and `DuplicateExternal`.

## Running Locally

Start PostgreSQL:

```bash
docker compose up -d postgres
```

Run the API:

```bash
dotnet run --project src/LedgerFlow
```

The application applies the PostgreSQL migration on startup.

Run the tests:

```bash
dotnet test
```

The test suite uses an isolated EF Core in-memory provider so API and domain tests do not require a running database.

The application exposes a health endpoint at `GET /health`.

### Observability

OpenTelemetry is registered for ASP.NET Core traces and metrics, with custom LedgerFlow business instruments for transaction outcomes, processing duration, retries, dead letters, reconciliation results, settlements, and fraud decisions. Structured request logs include the correlation identifier.

No telemetry backend is required for local startup. To export telemetry to an OTLP-compatible collector, set `OTEL_EXPORTER_OTLP_ENDPOINT` before starting the application.

## Project Structure

```text
LedgerFlow/
├── src/
│   └── LedgerFlow/
│       ├── Application/
│       │   ├── TransactionService.cs
│       │   ├── RetryService.cs
│       │   └── ReconciliationService.cs
│       ├── Domain/
│       │   ├── DomainModels.cs
│       │   ├── RetryModels.cs
│       │   └── ReconciliationModels.cs
│       ├── Infrastructure/
│       │   ├── LedgerFlowDbContext.cs
│       │   └── TransactionRepository.cs
│       ├── Migrations/
│       │   ├── 202609061830_InitialPersistentLedger.cs
│       │   ├── 202609091000_AddTransactionStateTransitions.cs
│       │   ├── 202609101000_AddEventOutbox.cs
│       │   ├── 202609141000_AddRetryRecovery.cs
│       │   └── 202609171000_AddReconciliation.cs
│       ├── LedgerFlow.csproj
│       ├── Program.cs
│       └── appsettings.json
├── tests/
│   └── LedgerFlow.Tests/
│       ├── PersistentLedgerTests.cs
│       ├── EventDrivenProcessingTests.cs
│       ├── RetryRecoveryTests.cs
│       ├── ReconciliationTests.cs
│       ├── TransactionApiTests.cs
│       ├── TransactionDomainTests.cs
│       └── LedgerFlow.Tests.csproj
├── docker-compose.yml
└── README.md
```

The implementation is intentionally decomposed into domain, application, infrastructure, API, and test boundaries so later phases can build on stable financial primitives.

# Roadmap / TBD

The following capabilities are planned as incremental work under the LedgerFlow epic.

## Phase 6 — Reconciliation Engine ✅

- [x] Introduce an external transaction/settlement input source
- [x] Compare completed internal transaction records against external records
- [x] Detect matched transactions
- [x] Detect missing internal transactions
- [x] Detect missing external transactions
- [x] Detect amount mismatches
- [x] Detect currency mismatches
- [x] Detect duplicate records
- [x] Persist reconciliation runs and results
- [x] Make reconciliation requests idempotent
- [x] Produce reconciliation results and discrepancy explanations

Implemented by Issue #19.

## Phase 7 — Settlement Simulation ⬜

- Create settlement batches
- Define settlement lifecycle states
- Select eligible transactions for settlement
- Calculate settlement totals
- Simulate successful and failed settlement runs
- Make settlement operations idempotent
- Track settlement discrepancies

## Phase 8 — Fraud-Rule Simulation ⬜

- Add configurable rule evaluation
- Add transaction threshold rules
- Add velocity checks
- Add account restrictions
- Record rule decisions

This phase is a simulation only and is not intended to represent production fraud detection.

## Phase 9 — Audit Trail ⬜

- Record transaction creation
- Record state transitions
- Record ledger posting
- Record retries and failures
- Record reconciliation outcomes
- Record settlement actions
- Make audit records append-oriented and traceable

## Phase 10 — Observability ✅

- [x] Structured HTTP logging with correlation identifiers
- [x] OpenTelemetry tracing for HTTP and key business operations
- [x] OpenTelemetry metrics for transactions, processing duration, retries, dead letters, reconciliation results, settlements, and fraud decisions
- [x] Request correlation IDs exposed through `X-Correlation-Id`
- [x] Optional OTLP export through `OTEL_EXPORTER_OTLP_ENDPOINT`
- [x] Transaction, retry, reconciliation, settlement, and fraud operations correlate with trace context
- [x] Observability configuration remains backend-neutral

## Phase 11 — API Hardening & Documentation ⬜

- Expand transaction, ledger, reconciliation, and settlement APIs
- Introduce consistent error responses
- Improve OpenAPI documentation
- Add realistic API examples
- Add pagination/filtering where appropriate
- Improve local development and configuration experience

## Phase 12 — Failure Lab & End-to-End Demonstrations ⬜

Create reproducible scenarios demonstrating the system's reliability characteristics:

- Duplicate transaction request
- Concurrent duplicate requests
- Duplicate event delivery
- Out-of-order event delivery
- Failed processing followed by retry
- Permanent failure routed to dead letter
- Missing event
- Duplicate external settlement record
- Ledger amount mismatch
- Partial settlement failure
- Successful recovery after transient infrastructure failure

# Financial Invariants

LedgerFlow will treat financial correctness as a first-class concern.

Key invariants include:

- Every posted journal transaction must balance.
- Total debits must equal total credits.
- Amounts must be positive and valid.
- Source and destination accounts must be valid and distinct where required.
- A transaction must not create duplicate financial effects.
- Posted journal entries should be immutable.
- Financial operations that require atomicity must execute transactionally.

# Distributed-System Principles

The project is intentionally designed to demonstrate realistic distributed-system trade-offs.

- Idempotency is preferred over assuming exactly-once processing.
- Consumers should tolerate at-least-once event delivery.
- External side effects should be idempotent.
- Database state and emitted events should be coordinated using an outbox pattern where appropriate.
- Failures should be observable and recoverable.
- Consistency requirements should be explicit rather than accidental.

# Testing Strategy

Testing will evolve alongside the architecture.

Current coverage includes:

- Successful transaction creation
- Balanced debit/credit ledger creation
- Idempotency behavior
- Invalid amount validation
- Domain-level financial invariants
- Persistence of transactions and journal entries
- Reconciliation matching and discrepancy detection
- Reconciliation idempotency

Future coverage will include:

- Concurrent idempotency against PostgreSQL
- Duplicate events
- Out-of-order events
- Retry behavior
- Dead-letter behavior
- Settlement idempotency
- End-to-end recovery scenarios

# Out of Scope

LedgerFlow does not aim to provide:

- Real payment-network integration
- Real banking settlement
- Production fraud detection
- Real customer financial data processing
- PCI compliance certification
- Production payment processing guarantees
- Real-money transfers

# Epic

The complete roadmap is tracked in:

**EPIC: LedgerFlow Transaction Processing & Reconciliation Platform**

https://github.com/hafeez-dev-labs/LedgerFlow/issues/3

The implementation will continue through focused issues and pull requests rather than attempting to build the entire platform in a single change.

# Engineering Objective

The end state is a coherent financial transaction processing simulation demonstrating:

```text
Financial Correctness
        +
Idempotency
        +
Double-Entry Accounting
        +
Distributed Processing
        +
Failure Recovery
        +
Reconciliation
        +
Settlement
        +
Auditability
        +
Observability
```

The project is intended to provide a practical platform for exploring backend architecture, distributed systems, financial-domain modeling, consistency, reliability, and API design.
