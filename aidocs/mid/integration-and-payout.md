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
2. Hub client delivers it to runtime.
3. `PayoutCoordinator` checks readiness and policy.
4. The built-in trade executor starts or monitors the outgoing trade.
5. Observed state changes are converted into payout events.
6. Events are stored in the durable outbox.
7. Events are sent to the backend and acknowledged.
8. Only acknowledged events may leave the outbox.
9. On `TradeCompleted` the backend queues the *next* leg, so multi-leg batches run strictly sequentially — one executor operation at a time.

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
- **Dependency note:** the `ECommons` NuGet DLL is tied to a Dalamud/FFXIVClientStructs version —
  keep the package version in sync with Dalamud updates to avoid runtime struct mismatches.

## Important Rules

- One active payout operation at a time.
- No automatic success on ambiguous outcomes.
- No backend acknowledgement means the event must remain replayable.
- Idempotency keys must stay stable for financial actions.
- Trade events are ignored unless operation id, executor instance, exact player identity, and amount all match the active leg.
- Failed / cancelled / timed-out unpaid legs fall back to a normal failed payout path; the backend returns the unpaid gross to the player and the dealer starts a fresh cash out for the remainder. Ambiguous outcomes are reported with `IsAmbiguous = true`; the backend releases the remainder but flags the membership (`ReconciliationState = "reviewNeeded"`, shown in the session panel) for in-game verification before re-paying.

## Where To Validate Changes

- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
