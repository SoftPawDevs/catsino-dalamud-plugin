# Backend Protocol v1

All JSON uses contract `1.3.0`, camel-case properties, string enums, GUID identifiers, signed 64-bit gil amounts, decimal percentages, and UTC `DateTimeOffset` values. The plugin is untrusted: every token count, membership, session transition, idempotency decision, payout amount, and reconciliation result remains backend-authoritative.

`backend-v1.fixture.json` is the machine-readable contract fixture. It enumerates every route, DTO example, hub event, hub payload, and idempotency requirement without requiring the private server to reference plugin source.

## HTTP Paths

- `POST /api/v1/dealers/authorize`
- `POST /api/v1/dealers/refresh`
- `POST /api/v1/dealers/disconnect`
- `POST /api/v1/plugin/pairings`
- `POST /api/v1/plugin/pairings/{pairingId}/heartbeat`
- `POST /api/v1/payout-executor/status`
- `GET /api/v1/game-sessions`
- `GET /api/v1/game-sessions/active`
- `GET /api/v1/game-sessions/{sessionId}`
- `POST /api/v1/game-sessions`
- `PATCH /api/v1/game-sessions/{sessionId}/fee`
- `POST /api/v1/game-sessions/{sessionId}/open`
- `POST /api/v1/game-sessions/{sessionId}/close`
- `GET /api/v1/game-sessions/{sessionId}/players`
- `GET /api/v1/game-sessions/{sessionId}/roster`
- `GET /api/v1/game-sessions/{sessionId}/invites`
- `POST /api/v1/game-sessions/{sessionId}/invites`
- `DELETE /api/v1/game-sessions/{sessionId}/invites/{inviteId}`
- `POST /api/v1/game-sessions/{sessionId}/deposits`
- `POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/balance-adjustments`
- `GET /api/v1/game-sessions/{sessionId}/players/{membershipId}/cashout-preview`
- `POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/cashouts`
- `DELETE /api/v1/game-sessions/{sessionId}`
- `POST /api/v1/payout-events`
- `POST /api/v1/payout-events/{operationId}/{sequenceNumber}/ack`
- `GET /api/v1/payout-operations/open` (legacy per-leg recovery)
- `POST /api/v1/cashouts/{operationId}/retry` (legacy per-leg retry)
- `POST /api/v1/payout-operations/{operationId}/reconcile` (legacy per-leg reconcile)
- `POST /api/v1/cashouts/{cashOutId}/settle`
- `GET /api/v1/cashouts/open`

Every financial mutation sends a UUID `Idempotency-Key`: session create/fee/open/close, legacy manual deposit, signed balance adjustment, dealer cash out, cash-out settlement, and the legacy per-leg payout event / acknowledgment / cashout retry / reconcile. A failed logical operation retains that exact key for HTTP or dealer retry. The cash-out settlement key is deterministic per `cashOutId`, so a resent settlement (retry or restart) is idempotent and never double-books. (Legacy per-leg keys are deterministic from `operationId` plus `sequenceNumber`.) Invite creation is intentionally non-financial and non-idempotent because its opaque single-use secret must be stored hash-only and a lost successful response cannot safely reproduce the raw token. No trade observation creates a deposit.

`SessionRosterDto` is the dealer table snapshot. The plugin renders only `Tokens` as the player's current available balance and never derives finance values. Positive adjustments are deposits, negative adjustments debit available Tokens, and zero is invalid. Dealer cash-out confirmation echoes the previewed gross, fee, and net; the backend rejects a changed quote. A successful empty-membership removal returns `204`, while a created cash-out returns `200 CashOutResponse`. The backend remains authoritative for token bounds, session deletion/archival, membership removal, cash-out previews, fees, payout bookkeeping, and payout state.

## Plugin Hub

The client connects to `/hubs/plugin` and handles:

- `RefreshDealerSessions`
- `QueuePayoutLeg`
- `CancelPayoutOperation`
- `SessionClosed`
- `DealerAuthorizationRevoked`
- `ReconnectRequired`

It reports `ReportPayoutExecutorStatus`, `ReportOutgoingTradeStatus`, `ReportOutboxStatus`, and `ReportDepositStatus`. `QueuePayoutLeg` and `CancelPayoutOperation` are legacy hub events from the per-leg push flow; the client-driven cash-out below no longer relies on them.

## Client-driven cash-out

The dealer cash-out is client-driven. `POST /cashouts` returns the full `CashOutResponse` leg plan (backend-authored net per leg); the plugin then runs every leg locally and sequentially through its built-in trade executor — never waiting on a backend push to advance — and reports the whole batch to the backend **once**, at the end (or when a leg fails), via `POST /cashouts/{cashOutId}/settle`. The settlement lists each attempted leg's outcome (`completed` | `failed` | `ambiguous`); the backend books its OWN stored amounts (the client never sends gil amounts), completes the paid legs, releases the definitely-untraded remainder, and quarantines any `ambiguous` leg (kept deducted, membership flagged for in-game verification). `GET /cashouts/open` lets the plugin reconcile its durable local batch plan against the backend after a restart.

## Payout Safety

The backend authors every amount; the client only executes and reports. A physically completed outgoing trade is never re-run:

- The plugin persists the whole batch plan durably before any trade, and marks a leg `Trading` durably BEFORE the executor is released to move gil (the `ConfirmTrade` barrier). So after any crash a leg caught mid-trade is treated as `ambiguous` (quarantined, never re-traded), and a completed leg is never repeated.
- Settlement is idempotent per `cashOutId` on the backend, so a resent settlement never double-books.
- On restart/reconnect the plugin resumes its durable batch: it finishes pending legs, quarantines a mid-trade leg, and retries the settlement until acknowledged.
- A definite failure releases the untraded remainder for a fresh cash-out; an ambiguous outcome is `reconciliationRequired` and is never represented as `completed` or automatically re-paid.
