# Integration And Payout Notes

## Backend Channels

- HTTP requests go through `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`.
- Realtime backend instructions go through `src/Catsino.Plugin/Backend/PluginHubClient.cs`.
- Hub event names and protocol glue live beside the hub client.

## Trade Executor Boundary

- Outgoing payout execution is built into the dealer plugin and remains payout-only.
- The plugin must not treat inbound trades as deposits or automatic credits.
- Structured trade observation and completion rules live in `src/Catsino.Plugin/Payout/`.

## Payout Execution Path

1. Backend queues a payout leg (one leg of a batch; a large net splits into `Net <= 1,000,000` legs).
2. Hub client delivers it to runtime (`QueuePayoutLeg`).
3. `PayoutCoordinator.StartBackendLegAsync` checks readiness and policy.
4. The built-in trade executor starts or monitors the outgoing trade.
5. Observed state changes are converted into payout events.
6. Events are stored in the durable outbox.
7. Events are sent to the backend and acknowledged.
8. Only acknowledged events may leave the outbox.
9. On `TradeCompleted` the backend queues the *next* leg, so multi-leg batches run strictly sequentially — one executor operation at a time.

### Next-leg start is not push-only

The real-time `QueuePayoutLeg` push is the fast path, but it is not the sole trigger. If it is missed
(e.g. a hub reconnect on the leg boundary), the leg would otherwise strand. Recovery closes that gap:

- `PollBackendStateAsync` (timer) and `SynchronizeAfterHubConnectionAsync` (reconnect) both **replay the
  durable outbox first**, then call `RecoverOpenPayoutAsync`.
- `RecoverOpenPayoutAsync` walks `GET /payout-operations/open` and, per operation, calls
  `PayoutCoordinator.ResumeBackendOperationAsync`, which returns `Reattached` (executor already driving it),
  `Started` (a never-begun leg started), `NeedsReconcile`, or `Skipped`.
- A settled leg also proactively triggers this via the coordinator's `onLegSettled` callback, so the next
  leg starts in ~1s without waiting for the poll.

### Duplicate-payout safety (a physically completed trade is never re-run)

- **Durable outbox start guard:** `StartBackendLegAsync` refuses to start a leg if the durable outbox still
  holds any unsent event for its `OperationId` (`IPayoutOutbox.HasPendingForOperationAsync`). This survives a
  plugin restart, unlike the in-memory `terminalOperations` / executor `usedOperationIds` guards, so a leg
  whose completion is still queued locally can never be re-traded before that completion is delivered.
- **Confirm barrier:** the executor does not move gil (`ConfirmTrade`) until its `TradeOpened` event is
  durably persisted; the coordinator signals this via `IPayoutTradeExecutor.MarkOpenEventPersisted`. Hence
  if gil could have moved, a durable trace always exists.
- **Never re-trade an opened leg:** `ResumeBackendOperationAsync` only *starts* a leg in `Queued` /
  `WaitingForPlayer`. A leg the backend reports as `TradeOpened` / `TradeLocked` (physically opened, gil may
  have moved) whose executor is gone returns `NeedsReconcile`; the runtime then calls the backend reconcile
  endpoint instead of re-trading.

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
- Sequential handoff safety: `PayoutCoordinator.StartBackendLegAsync` self-heals a stale `active`
  when the executor is already idle, so the next leg is never blocked by a dropped terminal event.
- Confirm barrier: `ConfirmTrade` (the node-3 lock button + `SelectYesno` — where gil actually moves)
  returns early until `IsOpenEventPersisted(operationId)` is true, i.e. the coordinator has durably written
  the `TradeOpened` event. This is the guarantee that a physically completed trade always leaves a durable
  trace, so restart-time recovery never re-runs it.
- **Dependency note:** the `ECommons` NuGet DLL is tied to a Dalamud/FFXIVClientStructs version —
  keep the package version in sync with Dalamud updates to avoid runtime struct mismatches.

## Important Rules

- One active payout operation at a time.
- No automatic success on ambiguous outcomes.
- No backend acknowledgement means the event must remain replayable.
- Idempotency keys must stay stable for financial actions.
- Trade events are ignored unless operation id, executor instance, exact player identity, and amount all match the active leg.
- A leg with any unsent durable outbox event for its `OperationId` is never (re)started — the durable outbox, not in-memory state, is the cross-restart source of truth (`HasPendingForOperationAsync`).
- Recovery only *starts* `Queued` / `WaitingForPlayer` legs. A physically-opened (`TradeOpened` / `TradeLocked`) leg whose executor is gone is handed to backend reconciliation (`POST /api/v1/payout-operations/{operationId}/reconcile`), never re-traded.
- Failed / cancelled / timed-out unpaid legs fall back to a normal failed payout path; the backend returns the unpaid gross to the player and the dealer starts a fresh cash out for the remainder. Ambiguous outcomes are reported with `IsAmbiguous = true`; the backend releases the remainder but flags the membership (`ReconciliationState = "reviewNeeded"`, shown in the session panel) for in-game verification before re-paying. The plugin-driven reconcile path reaches the same review flag for a leg whose outcome could not be confirmed after recovery, but the backend quarantines the uncertain leg (its gross is *not* refunded) while releasing only the definitely-untraded later legs.

## Where To Validate Changes

- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
