# Backend Protocol v1

All JSON uses contract `1.0.0`, camel-case properties, string enums, GUID identifiers, signed 64-bit gil amounts, decimal percentages, and UTC `DateTimeOffset` values. The plugin is untrusted: every balance, membership, session transition, idempotency decision, payout amount, and reconciliation result remains backend-authoritative.

`backend-v1.fixture.json` is the machine-readable contract fixture. It enumerates every route, DTO example, hub event, hub payload, and idempotency requirement without requiring the private server to reference plugin source.

## HTTP Paths

- `POST /api/v1/dealers/authorize`
- `POST /api/v1/dealers/refresh`
- `POST /api/v1/dealers/disconnect`
- `POST /api/v1/plugin/pairings`
- `POST /api/v1/plugin/pairings/{pairingId}/heartbeat`
- `POST /api/v1/dropbox/status`
- `GET /api/v1/game-sessions`
- `GET /api/v1/game-sessions/active`
- `GET /api/v1/game-sessions/{sessionId}`
- `POST /api/v1/game-sessions`
- `PATCH /api/v1/game-sessions/{sessionId}/fee`
- `POST /api/v1/game-sessions/{sessionId}/open`
- `POST /api/v1/game-sessions/{sessionId}/close`
- `GET /api/v1/game-sessions/{sessionId}/players`
- `POST /api/v1/game-sessions/{sessionId}/invites`
- `POST /api/v1/game-sessions/{sessionId}/deposits`
- `POST /api/v1/payout-events`
- `POST /api/v1/payout-events/{operationId}/{sequenceNumber}/ack`
- `GET /api/v1/payout-operations/open`
- `POST /api/v1/cashouts/{operationId}/retry`
- `POST /api/v1/cashouts/{operationId}/reconciliation`

Every financial mutation sends a UUID `Idempotency-Key`: session create/fee/open/close, manual deposit, payout event, payout acknowledgment, cashout retry, and reconciliation. A failed logical operation retains that exact key for HTTP or dealer retry. Payout event keys are deterministic from `operationId` and `sequenceNumber`; acknowledgments use a separate deterministic purpose. Invite creation is intentionally non-financial and non-idempotent because its opaque single-use secret must be stored hash-only and a lost successful response cannot safely reproduce the raw token. No trade observation creates a deposit.

## Plugin Hub

The client connects to `/hubs/plugin` and handles:

- `RefreshDealerSessions`
- `QueuePayoutLeg`
- `CancelPayoutOperation`
- `RequestPayoutReconciliation`
- `SessionClosed`
- `DealerAuthorizationRevoked`
- `ReconnectRequired`

It reports `ReportDepositStatus`, `ReportDropboxStatus`, `ReportOutgoingTradeStatus`, and `ReportOutboxStatus`. Financial payout events are atomically persisted before transport, keyed by `operationId` and `sequenceNumber`, replayed in order, and removed only after an exact backend acknowledgment.

Dropbox capabilities include the nullable `pluginInstanceId` of the active IPC provider. When available, it must exactly match `PayoutEventDto.pluginInstanceId`; unavailable Dropbox capabilities use `null`.

## Payout Safety

The backend supplies one exact leg. The plugin verifies the operation, leg, session, character name, Home World, amount, Dropbox instance, IPC version, build version, and capabilities. A definite failure can only be retried through the dealer-triggered backend retry path. An ambiguous result is `reconciliationRequired` and is never represented as `tradeCompleted` or automatically retried.
