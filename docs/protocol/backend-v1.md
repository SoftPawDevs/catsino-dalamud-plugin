# Backend Protocol v1

All JSON uses contract `1.2.0`, camel-case properties, string enums, GUID identifiers, signed 64-bit gil amounts, decimal percentages, and UTC `DateTimeOffset` values. The plugin is untrusted: every token count, membership, session transition, idempotency decision, payout amount, and reconciliation result remains backend-authoritative.

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
- `GET /api/v1/payout-operations/open`
- `POST /api/v1/cashouts/{operationId}/retry`

Every financial mutation sends a UUID `Idempotency-Key`: session create/fee/open/close, legacy manual deposit, signed balance adjustment, dealer cash out, payout event, payout acknowledgment, and cashout retry. A failed logical operation retains that exact key for HTTP or dealer retry. Payout event keys are deterministic from `operationId` and `sequenceNumber`; acknowledgments use a separate deterministic purpose. Invite creation is intentionally non-financial and non-idempotent because its opaque single-use secret must be stored hash-only and a lost successful response cannot safely reproduce the raw token. No trade observation creates a deposit.

`SessionRosterDto` is the dealer table snapshot. The plugin renders only `Tokens` as the player's current available balance and never derives finance values. Positive adjustments are deposits, negative adjustments debit available Tokens, and zero is invalid. Dealer cash-out confirmation echoes the previewed gross, fee, and net; the backend rejects a changed quote. A successful empty-membership removal returns `204`, while a created cash-out returns `200 CashOutResponse`. The backend remains authoritative for token bounds, session deletion/archival, membership removal, cash-out previews, fees, payout bookkeeping, and payout state.

## Plugin Hub

The client connects to `/hubs/plugin` and handles:

- `RefreshDealerSessions`
- `QueuePayoutLeg`
- `CancelPayoutOperation`
- `SessionClosed`
- `DealerAuthorizationRevoked`
- `ReconnectRequired`

It reports `ReportDepositStatus`, `ReportPayoutExecutorStatus`, `ReportOutgoingTradeStatus`, and `ReportOutboxStatus`. Financial payout events are atomically persisted before transport, keyed by `operationId` and `sequenceNumber`, replayed in order, and removed only after an exact backend acknowledgment.

## Payout Safety

The backend supplies one exact leg. The plugin verifies the operation, leg, session, character name, Home World, amount, and payout executor instance. A definite failure can only be retried through the dealer-triggered backend retry path. An ambiguous result is `reconciliationRequired` and is never represented as `tradeCompleted` or automatically retried.
