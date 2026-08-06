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

1. Backend queues a payout leg.
2. Hub client delivers it to runtime.
3. `PayoutCoordinator` checks readiness and policy.
4. The built-in trade executor starts or monitors the outgoing trade.
5. Observed state changes are converted into payout events.
6. Events are stored in the durable outbox.
7. Events are sent to the backend and acknowledged.
8. Only acknowledged events may leave the outbox.

## Important Rules

- One active payout operation at a time.
- No automatic success on ambiguous outcomes.
- No backend acknowledgement means the event must remain replayable.
- Idempotency keys must stay stable for financial actions.
- Trade events are ignored unless operation id, executor instance, exact player identity, and amount all match the active leg.
- There is no reconciliation workflow. Failed or ambiguous unpaid legs fall back to a normal failed payout path, and the dealer retries by starting a fresh cash out for the returned available amount.

## Where To Validate Changes

- `tests/Catsino.Plugin.Tests/PayoutExecutionPolicyTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`
- `tests/Catsino.Plugin.Tests/PayoutCoordinatorTests.cs`
