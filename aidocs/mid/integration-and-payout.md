# Integration And Payout Notes

## Backend Channels

- HTTP requests go through `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`.
- Realtime backend instructions go through `src/Catsino.Plugin/Backend/PluginHubClient.cs`.
- Hub event names and protocol glue live beside the hub client.

## Blackjack Table Integration

The Blackjack dealer table is a read-mostly projection plus three dealer mutations. The plugin never deals cards
or values hands — the backend owns the shoe, the hand values, the 45s turn clock, and settlement.

- **Reads.** `CatsinoApiClient.GetBlackjackTableAsync(sessionId)` (`GET api/v1/game-sessions/{sessionId}/blackjack`)
  returns the dealer `BlackjackTableDto` — the dealer's own full hand is included (unlike the player projection,
  which hides the hole card during play); the shoe is never present.
- **Mutations** (idempotency-keyed): `DealBlackjackAsync` (`POST …/blackjack/deal`), `DealerBlackjackHitAsync`
  (`POST …/blackjack/hit`), `DealerBlackjackStayAsync` (`POST …/blackjack/stay`). Each returns the refreshed
  dealer table. Deal only includes seats that placed a bet.
- **Live updates, two ways.** The backend device-pushes `BlackjackStateChanged` (`PluginHubProtocol.BlackjackStateChanged`,
  wired in `PluginHubClient` → `blackjackStore.Set(table)`), and `CatsinoRuntime.RefreshTableGamesAsync`
  polls tracked turn-based sessions about every ~2s. The poll is the belt-and-suspenders guarantee that the
  Hit/Stay controls reflect whose turn it truly is even if a single live push is missed.
- **State store.** `Runtime/BlackjackTableStore.cs` holds the latest `BlackjackTableDto` per session; the UI
  (`BlackjackPanelRenderer`) enables Hit/Stay only when the table status is `dealerTurn`.

## Roulette Table Integration

Roulette is read-mostly plus exactly one dealer mutation, and — unlike Hold'em — the dealer's payload is the
same one the players get: every chip, whose it is, and the winning number. There is nothing to withhold.

- **Reads.** `CatsinoApiClient.GetRouletteTableAsync(sessionId)` (`GET api/v1/game-sessions/{sessionId}/roulette`)
  returns the shared `RouletteTableDto`: every bet with its owner's name, the total staked, the winning
  number once the wheel is released, the recent numbers, and the table's bet limits.
- **Mutation** (idempotency-keyed): `SpinRouletteAsync` (`POST .../roulette/spin`) releases the ball. The
  backend draws the number immediately but books the payouts only when the 8s deadline passes, so the spin
  is not a settlement.
- **Live updates, two ways.** The backend device-pushes `RouletteStateChanged`
  (`PluginHubProtocol.RouletteStateChanged`, wired in `PluginHubClient` -> `rouletteStore.Set(table)`), plus
  the same ~2s `RefreshTableGamesAsync` poll.
- **State store.** `Runtime/RouletteTableStore.cs` holds the latest `RouletteTableDto` per session; the UI
  (`RoulettePanelRenderer`) enables **Spin** only while betting is open and at least one chip is down.

## Manual Settlement Integration (a payout made outside the game)

- **Read.** The quote comes from the ordinary `GetPlayerCashOutPreviewAsync` — the same gross/fee/net a
  normal cash-out would produce, because the fee applies either way.
- **Mutation** (idempotency-keyed): `SettleManuallyAsync`
  (`POST api/v1/game-sessions/{sessionId}/players/{membershipId}/manual-settlement`) with
  `ManualSettlementRequest(confirmAllAvailable, expectedGross, expectedFee, expectedNet)`. The echo is what
  lets the backend refuse a settlement whose amount moved between the dealer reading it and confirming it.
- **No payout batch is created and nothing is traded**, so `PayoutBatchCoordinator` is not involved at all —
  this is the one payout path the plugin does not execute. The roster refreshes afterwards and the player is
  gone from the table.
- **Failure handling** matches a cash-out: an ambiguous failure keeps the submission and its idempotency key
  so the retry is the same operation, not a second payout.

## Texas Hold'em Table Integration

Hold'em is read-mostly plus exactly one dealer mutation. Players play each other for the pot, so the plugin
neither deals nor decides anything: the backend owns the deck, the board, every betting rule, the 45s action
clock and the side-pot settlement.

- **Reads.** `CatsinoApiClient.GetHoldemTableAsync(sessionId)` (`GET api/v1/game-sessions/{sessionId}/holdem`)
  returns the dealer `HoldemTableDto`. **It contains no hole card at all** — not during the hand, not at
  showdown — and never the deck. The dealer plays no hand, so they never need one; withholding them is the
  only way they cannot be leaked. `HoldemSeatDto.HasHiddenCards` is what the renderer turns into face-down backs.
- **Mutation** (idempotency-keyed): `DealHoldemAsync` (`POST …/holdem/deal`) starts the next hand and returns
  the refreshed dealer table. There is no dealer hit/stay: the backend deals the flop/turn/river itself as
  each betting round closes.
- **Live updates, two ways.** The backend device-pushes `HoldemStateChanged`
  (`PluginHubProtocol.HoldemStateChanged`, wired in `PluginHubClient` → `holdemStore.Set(table)`), plus the
  same ~2s `RefreshTableGamesAsync` poll.
- **State store.** `Runtime/HoldemTableStore.cs` holds the latest `HoldemTableDto` per session; the UI
  (`HoldemPanelRenderer`) enables **Deal** only when no hand is running and at least two seated players have
  chips.
- **Seat cap.** Ten players per table (`HoldemBetDefaults.MaxSeats`), the dealer excluded. Enforced by the
  backend at seating and at session creation; surfaced early by `DealerInputValidator`.

## Trade Executor Boundary

- Outgoing payout execution is built into the dealer plugin and remains payout-only.
- The plugin must not treat inbound trades as deposits or automatic credits.
- Structured trade observation and completion rules live in `src/Catsino.Plugin/Payout/`.

## Payout Execution Path (client-driven batch)

The cash-out is **client-driven**: the plugin owns the whole leg sequence, and the backend is contacted only
at the end (or on a leg failure). Orchestration lives in `src/Catsino.Plugin/Payout/PayoutBatchCoordinator.cs`
with durable state in `PersistentCashOutBatchStore` (`Payout/CashOutBatch.cs`).

1. The dealer submits a cash-out; `POST /cashouts` returns the full `CashOutResponse` leg plan (backend-authored
   net per leg; a large net splits into `Net <= 1,000,000` legs).
2. `CatsinoRuntime.SubmitCashOutAsync` builds a `CashOutBatchPlan` (using the roster player's exact name/world)
   and calls `PayoutBatchCoordinator.StartBatchAsync`.
3. The batch plan is persisted durably BEFORE any trade, then legs run **one at a time** through the built-in
   trade executor (`StartOperation`) — never waiting on a backend push to advance.
4. Per leg, on `TradeOpened` the coordinator marks the leg `Trading` durably and releases the executor's confirm
   barrier (`MarkOpenEventPersisted`); on `TradeCompleted` it marks the leg `Completed` durably and starts the
   next pending leg.
5. When every leg completes — or a leg fails/is ambiguous — the coordinator reports the whole batch **once** via
   `POST /cashouts/{cashOutId}/settle`, retrying durably until acknowledged, then deletes the durable batch.

The legacy per-leg push flow (`QueuePayoutLeg` hub event, `payout-events`, `payout-operations/open`, `retry`,
`reconcile`) still exists on the backend but is no longer used by the cash-out path.

### Restart / reconnect recovery

`PollBackendStateAsync` (timer) and `SynchronizeAfterHubConnectionAsync` (reconnect) call
`PayoutBatchCoordinator.ResumeAsync`, which loads the durable batch and: quarantines a leg caught mid-trade,
settles a fully-resolved batch, or resumes the next pending leg. `GET /cashouts/open` is a backend cross-check.

### Duplicate-payout safety (a physically completed trade is never re-run)

- **Durable batch plan** (`PersistentCashOutBatchStore`, atomic WriteThrough writes) is the cross-restart truth
  about which legs already traded — it survives a plugin restart, unlike in-memory state.
- **Confirm barrier:** the executor does not move gil (`ConfirmTrade`) until the leg is durably marked `Trading`
  and the coordinator signals `IPayoutTradeExecutor.MarkOpenEventPersisted`. So any trade that could have moved
  gil always leaves a durable `Trading` marker.
- **Never re-trade an opened leg:** on restart, a leg still `Trading` (gil may have moved) is quarantined as
  `ambiguous` and reported to `settle` — never re-traded; a `Completed` leg is never repeated.
- **Idempotent settlement:** the settle key is deterministic per `cashOutId` (`FinancialIdempotency.ForCashOutSettlement`),
  so a resent settlement never double-books.

## Trade-driving details (`BuiltInPayoutTradeExecutor`, ECommons-based)

- The outgoing trade is driven by **ECommons** (`ECommonsMain.Init` in `Plugin.cs`) using its
  NeoTaskManager + trade primitives.
  Per leg the executor enqueues: target the exact player + `/trade` → wait `ConditionFlag.TradeOpen`
  → `Callback.Fire` the gil input → `Callback.Fire` the exact amount → `ConfirmTrade` (press the
  node-3 lock button via `ClickAddonButton`, accept the `SelectYesno` via `AddonMaster.SelectYesno.Yes()`)
  → wait for the trade to close → resolve. `EzThrottler`/`FrameThrottler` pace the actions.
- The executor runs a framework-thread supervisor (`OnFrameworkUpdate`) that enqueues the sequence
  (StartOperation is called off-thread), performs dealer-requested aborts, and — if the sequence
  ends via timeout/abort without the resolve step — decides the terminal outcome.
- **Abort always succeeds (1.4.1):** `CancelOperation` no longer refuses when a (possibly stale) flag
  reports the trade window open. It always accepts, and the supervisor decides the outcome from the
  ACTUAL gil movement (`ResolveOutcome`): a never-opened trade releases cleanly, a real transfer settles
  as completed/ambiguous. `PayoutBatchCoordinator.AbortActiveAsync` also releases the remainder itself
  (fails the current not-yet-traded leg + settles) when no executor operation is in flight — so the dealer
  can always abort and the unpaid remainder returns to the player's tokens for a fresh cash-out.
- **Financial proof is independent of the clicks:** the terminal outcome comes only from
  `TradeCloseEvaluator` (exact gil debit + accepted confirmation → `TradeCompleted`; unchanged +
  unconfirmed → `TradeCancelled`; anything else → reconciliation). A button press is never proof.
  `PayoutTradeEvent`s (PlayerDetected / TradeOpened / TradeCompleted / Cancelled / Failed / TimedOut)
  are still raised for the backend with monotonic sequence numbers.
- Sequential handoff: `PayoutBatchCoordinator` starts the next pending leg only after the current leg's
  terminal event, so one executor operation runs at a time; the client owns the sequencing (no backend push).
- Confirm barrier: `ConfirmTrade` (the node-3 lock button + `SelectYesno` — where gil actually moves)
  returns early until `IsOpenEventPersisted(operationId)` is true, i.e. the coordinator has durably marked the
  leg `Trading`. This is the guarantee that a physically completed trade always leaves a durable trace, so
  restart-time recovery never re-runs it.
- **Dependency note:** the `ECommons` NuGet DLL is tied to a Dalamud/FFXIVClientStructs version —
  keep the package version in sync with Dalamud updates to avoid runtime struct mismatches.

## Important Rules

- One leg trades at a time; the client sequences the whole batch and settles once.
- Amounts are backend-authored; the client only reports each leg's outcome (`completed` | `failed` | `ambiguous`).
- No automatic success on ambiguous outcomes.
- Idempotency keys must stay stable for financial actions; the settle key is deterministic per `cashOutId`.
- Trade events are ignored unless the operation id and exact player identity/amount match the active leg.
- The durable batch plan (not in-memory state) is the cross-restart source of truth; a leg marked `Trading`
  before a crash is quarantined as `ambiguous`, never re-traded, and a `Completed` leg is never repeated.
- A clean failed/cancelled/timed-out leg releases the untraded remainder for a fresh cash out. An abort
  always releases the unpaid remainder to the player's tokens (outcome decided by actual gil movement, 1.4.1).
  The client reports `ambiguous` for an unverifiable trade; how the backend books an ambiguous leg is the
  backend's decision (the active client-driven settle path refunds it — see the web repo's `mid/domain-and-state.md`).

## Where To Validate Changes

- `tests/Catsino.Plugin.Tests/PayoutBatchCoordinatorTests.cs` (batch orchestration, crash-safety, settlement)
- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs` (legacy per-leg coordinator)
