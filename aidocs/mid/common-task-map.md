# Common Task Map

## Authorization And Pairing

- `src/Catsino.Plugin/Ui/CatsinoWindow.cs`
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`
- `tests/Catsino.Plugin.Tests/ValidationAndSecurityTests.cs`

## Session Lists, Selection, Roster

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Runtime/SessionRosterStore.cs`
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
- `tests/Catsino.Plugin.Tests/DealerSessionStateTests.cs`

## Invites And Tell Command Flow

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Runtime/GameChat.cs`
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`

Notes:
Invite creation now depends on exact `Character Name`, exact `Home World`, and an explicit starting balance. The plugin UI and runtime both reject duplicate invites when the roster already shows the player as active or pending.

**Reinvite** is the deliberate exception: the per-player roster row has a "Reinvite" button (`SessionPanelRenderer.DrawPlayerRow`) that calls `CatsinoRuntime.ReinviteAndTellAsync(sessionId, membershipId, name, world)` → `CatsinoApiClient.ReinviteAsync` (`POST api/v1/game-sessions/{sessionId}/players/{membershipId}/reinvite`). It bypasses the duplicate/active guard on purpose (redeeming resumes the active membership, wallet kept) and `/tell`s the fresh link.

## Deposits And Dealer Financial Actions

- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- `src/Catsino.Plugin/Workflow/DealerSessionActions.cs`
- `src/Catsino.Plugin/Workflow/DepositSubmission.cs`

## Payout Execution (client-driven cash-out batch)

- `src/Catsino.Plugin/Payout/PayoutBatchCoordinator.cs` (runs the whole batch locally, settles once)
- `src/Catsino.Plugin/Payout/CashOutBatch.cs` (durable batch plan/store + settlement transport)
- `src/Catsino.Plugin/Payout/BuiltInPayoutTradeExecutor.cs`
- `src/Catsino.Plugin/Payout/PayoutExecutionPolicy.cs`
- `src/Catsino.Plugin/Payout/PayoutTradeModels.cs`
- `src/Catsino.Plugin/Payout/PayoutCoordinator.cs` (legacy per-leg push flow, no longer used by cash-out)

## Durable Outbox And Replay

- `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`
- `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`
- `tests/Catsino.Plugin.Tests/PersistentPayoutOutboxTests.cs`

## Hub Reconnection And Recovery

- `src/Catsino.Plugin/Backend/PluginHubClient.cs`
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`PollBackendStateAsync`, `SynchronizeAfterHubConnectionAsync` → `PayoutBatchCoordinator.ResumeAsync`; `RequestDealerRefreshAsync` debounces roster pushes)
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs` (`SettleCashOutAsync`, `GetOpenCashOutsAsync`)

## Protocol Shape And Compatibility

- `src/Catsino.Plugin.Contracts/`
- `docs/protocol/backend-v1.md`
- `tests/Catsino.Plugin.Tests/ApiProtocolTests.cs`
- `tests/Catsino.Plugin.Tests/ProtocolFixtureTests.cs`
