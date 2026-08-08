# Integration And Payout Notes

## Backend Channels

- HTTP requests go through `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`.
- Realtime backend instructions go through `src/Catsino.Plugin/Backend/PluginHubClient.cs`.
- Hub event names and protocol glue live beside the hub client.

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
  (StartOperation is called off-thread), performs backend-requested aborts, and — if the sequence
  ends via timeout/abort without the resolve step — decides the terminal outcome.
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
- A clean failed/cancelled/timed-out leg releases the untraded remainder for a fresh cash out; an ambiguous
  leg is quarantined on the backend (its gross is *not* refunded) and the membership is flagged
  (`ReconciliationState = "reviewNeeded"`, shown in the session panel) for in-game verification before re-paying.

## Where To Validate Changes

- `tests/Catsino.Plugin.Tests/PayoutBatchCoordinatorTests.cs` (batch orchestration, crash-safety, settlement)
- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs` (legacy per-leg coordinator)
