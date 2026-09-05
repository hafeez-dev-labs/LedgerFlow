# LedgerFlow

A small financial transaction processing and reconciliation engine, built incrementally to explore financial correctness and distributed-systems concepts.

## Phase 1

The initial implementation provides an in-memory transaction API with:

- Transaction validation
- Idempotency via `Idempotency-Key`
- Explicit transaction states
- Double-entry debit/credit ledger entries
- Transaction lookup
- Automated API tests

```text
POST /transactions
        |
        v
   Validation
        |
        v
   Idempotency
        |
        v
  Transaction
        |
        v
 Double-entry Ledger
```

## Run

```bash
dotnet run --project src/LedgerFlow
```

The API exposes `GET /health` and the transaction endpoints.

## Test

```bash
dotnet test
```

## Example

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

Persistence, messaging, reconciliation workflows, settlement, fraud rules, and distributed workers will be introduced in later increments.
