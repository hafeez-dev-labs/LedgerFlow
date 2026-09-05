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

### Phase 1 — Transaction Processing Foundation ✅

The first implementation is complete and merged. It currently provides:

- ASP.NET Core Web API
- Transaction creation API
- Transaction lookup API
- Request validation
- Required `Idempotency-Key` handling
- Transaction lifecycle states
- In-memory transaction storage
- Basic double-entry ledger
- Balanced debit and credit entries
- Health endpoint
- Automated API tests

Current API surface:

```text
POST /transactions
GET  /transactions/{id}
GET  /health
```

Example request:

```http
POST /transactions
Idempotency-Key: order-10001
Content-Type: application/json

{
  "fromAccount": "customer-001",
  "toAccount": "merchant-001",
  "amount": 100.50,
  "currency": "USD"
}
```

A successful transaction creates two ledger entries:

```text
customer-001   Debit   100.50 USD
merchant-001   Credit  100.50 USD

Net: 0.00 USD
```

## Running Locally

Run the API:

```bash
dotnet run --project src/LedgerFlow
```

Run the tests:

```bash
dotnet test
```

The application exposes a health endpoint at `GET /health`.

## Project Structure

```text
LedgerFlow/
├── src/
│   └── LedgerFlow/
│       ├── LedgerFlow.csproj
│       └── Program.cs
├── tests/
│   └── LedgerFlow.Tests/
│       ├── LedgerFlow.Tests.csproj
│       └── TransactionApiTests.cs
└── README.md
```

The current implementation intentionally keeps the first increment small. The architecture will be decomposed into clearer domain, application, infrastructure, and API boundaries as the system grows.

# Roadmap / TBD

The following capabilities are planned as incremental work under the LedgerFlow epic.

## Phase 2 — Domain & Persistent Ledger ⬜

- Introduce explicit domain models and invariants
- Replace in-memory storage with PostgreSQL
- Add durable transaction and ledger storage
- Add database transactions for atomic financial operations
- Add database constraints and indexes
- Make journal entries immutable after posting

## Phase 3 — Transaction State Machine ⬜

- Formalize valid state transitions
- Support `Pending → Processing → Completed / Failed`
- Reject invalid transitions
- Record transition timestamps
- Capture failure reasons

## Phase 4 — Event-Driven Processing & Outbox ⬜

- Introduce asynchronous transaction processing
- Add a message broker
- Publish transaction lifecycle events
- Implement idempotent consumers
- Add transactional outbox processing
- Model at-least-once delivery semantics

## Phase 5 — Retry / Dead Letter / Failure Recovery ⬜

- Add transient failure handling
- Implement bounded retries with backoff
- Track retry attempts
- Capture failure reasons
- Introduce dead-letter handling
- Demonstrate recovery from transient failures

## Phase 6 — Reconciliation Engine ⬜

- Introduce an external transaction/settlement input source
- Compare internal ledger records against external records
- Detect matched transactions
- Detect missing internal transactions
- Detect missing external transactions
- Detect amount mismatches
- Detect currency mismatches
- Detect duplicate records
- Produce reconciliation results and discrepancy explanations

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

## Phase 10 — Observability ⬜

- Add structured logging
- Add OpenTelemetry tracing
- Add application metrics
- Measure transaction latency
- Measure success/failure rates
- Measure retry activity
- Measure dead-letter depth
- Measure reconciliation mismatch rates
- Measure settlement success/failure
- Measure processing throughput
- Correlate activity using transaction and correlation identifiers

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

Future coverage will include:

- Financial invariants
- Concurrent idempotency
- State-machine transitions
- Duplicate events
- Out-of-order events
- Retry behavior
- Dead-letter behavior
- Reconciliation discrepancies
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