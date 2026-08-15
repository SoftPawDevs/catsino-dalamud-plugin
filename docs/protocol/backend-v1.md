# Backend Protocol v1

All JSON uses contract `1.10.0` (backend accepts plugins on `{1.9.0, 1.10.0}`), camel-case properties, string enums, GUID identifiers, signed 64-bit gil amounts, decimal percentages, and UTC `DateTimeOffset` values. The plugin is untrusted: every token count, membership, session transition, idempotency decision, payout amount, and reconciliation result remains backend-authoritative.

Recent additions: `1.4.0` added an optional `maxPlayers` to `CreateGameSessionRequest`/`GameSessionDto` (null = unlimited); `1.5.0` added the Blackjack dealer surface; `1.7.0` added the Texas Hold'em dealer surface (`HoldemTableDto` and friends); `1.8.0` added `GameSessionDto.DealerSessionNumber`, the per-dealer "#1", "#2" label; `1.9.0` added the Roulette dealer surface (`RouletteTableDto` and friends) and `ManualSettlementRequest`; `1.10.0` replaces `RouletteBetDefaults.SpinSeconds` with `SpinMilliseconds` (6600 — the exact length of the spin sound both surfaces play). `gameType` is one of `plinko`, `blackjack`, `holdem`, `roulette`.

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
- `GET /api/v1/game-sessions/{sessionId}/blackjack`
- `POST /api/v1/game-sessions/{sessionId}/blackjack/deal`
- `POST /api/v1/game-sessions/{sessionId}/blackjack/hit`
- `POST /api/v1/game-sessions/{sessionId}/blackjack/stay`
- `GET /api/v1/game-sessions/{sessionId}/holdem`
- `POST /api/v1/game-sessions/{sessionId}/holdem/deal`
- `GET /api/v1/game-sessions/{sessionId}/roulette`
- `POST /api/v1/game-sessions/{sessionId}/roulette/spin`
- `POST /api/v1/game-sessions/{sessionId}/deposits`
- `POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/balance-adjustments`
- `GET /api/v1/game-sessions/{sessionId}/players/{membershipId}/cashout-preview`
- `POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/cashouts`
- `POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/manual-settlement`
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
- `BlackjackStateChanged`
- `HoldemStateChanged`

It reports `ReportPayoutExecutorStatus`, `ReportOutgoingTradeStatus`, `ReportOutboxStatus`, and `ReportDepositStatus`. `QueuePayoutLeg` and `CancelPayoutOperation` are legacy hub events from the per-leg push flow; the client-driven cash-out below no longer relies on them.

## Table games

Blackjack, Texas Hold'em and Roulette are entirely backend-driven: the backend shuffles, deals, values every hand, draws the winning pocket, runs the clocks and books every payout. The plugin renders a projection and submits the dealer's controls.

- **Blackjack** — the dealer plays a hand, so `Deal` / `Hit` / `Stay` are all dealer actions and `BlackjackTableDto` reveals the dealer's own two cards (the players' view hides the hole card until the dealer's turn).
- **Hold'em** — players play each other for the pot, so the dealer only starts hands (`deal`); the backend runs the flop/turn/river as each betting round closes. `HoldemTableDto` is projected per audience and the **dealer audience never receives a hole card**, not even at showdown: the dealer does not need one, and withholding it is the only way it cannot leak. Blinds derive from the session's `minBet` (big blind = `minBet`, small blind = half). A Hold'em table seats at most 10 players; the backend clamps `maxPlayers` accordingly at session creation.
- **Roulette** — a shared round against the house on a fair European wheel (37 pockets, single zero, 36/37 expected return on every field). The dealer only releases the ball (`spin`). Nothing at this table is secret, so `RouletteTableDto` is ONE shared view: it carries every player's bets with their owner's name, and the dealer receives exactly what the players do. `winningNumber` is populated the moment the wheel is released — the clients need it to animate the ball — but the payouts are only booked when `deadlineAt` passes (8s), by which point betting has long closed. The results stay visible for 10s and the round then reopens by itself. The spin lasts `RouletteBetDefaults.SpinMilliseconds`.

## Manual settlement

`POST /api/v1/game-sessions/{sessionId}/players/{membershipId}/manual-settlement` books a payout the dealer made **outside the game** (a marketboard sale, when the amount dwarfs what 1M-per-trade payouts can carry) and clears the player from the table. It is the one payout path with no batch, no leg and no trade: the plugin fetches the ordinary cash-out quote, shows the dealer the exact **net to pay**, the dealer performs the trade themselves, and only then confirms. `ManualSettlementRequest` echoes `expectedGross`/`expectedFee`/`expectedNet` so the backend refuses if the balance moved in between; it also refuses while a cash-out is active or while any chips are still reserved in a live round. The dealer fee applies exactly as it would for a traded cash-out.

## Client-driven cash-out

The dealer cash-out is client-driven. `POST /cashouts` returns the full `CashOutResponse` leg plan (backend-authored net per leg); the plugin then runs every leg locally and sequentially through its built-in trade executor — never waiting on a backend push to advance — and reports the whole batch to the backend **once**, at the end (or when a leg fails), via `POST /cashouts/{cashOutId}/settle`. The settlement lists each attempted leg's outcome (`completed` | `failed` | `ambiguous`); the backend books its OWN stored amounts (the client never sends gil amounts), completes the paid legs, releases the definitely-untraded remainder, and quarantines any `ambiguous` leg (kept deducted, membership flagged for in-game verification). `GET /cashouts/open` lets the plugin reconcile its durable local batch plan against the backend after a restart.

## Payout Safety

The backend authors every amount; the client only executes and reports. A physically completed outgoing trade is never re-run:

- The plugin persists the whole batch plan durably before any trade, and marks a leg `Trading` durably BEFORE the executor is released to move gil (the `ConfirmTrade` barrier). So after any crash a leg caught mid-trade is treated as `ambiguous` (quarantined, never re-traded), and a completed leg is never repeated.
- Settlement is idempotent per `cashOutId` on the backend, so a resent settlement never double-books.
- On restart/reconnect the plugin resumes its durable batch: it finishes pending legs, quarantines a mid-trade leg, and retries the settlement until acknowledged.
- A definite failure releases the untraded remainder for a fresh cash-out; an ambiguous outcome is `reconciliationRequired` and is never represented as `completed` or automatically re-paid.
