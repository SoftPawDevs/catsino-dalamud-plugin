# File Map

## Entry And UI

- `src/Catsino.Plugin/Plugin.cs`: plugin entry point, command registration, window system integration.
- `src/Catsino.Plugin/Ui/CatsinoWindow.cs`: main dealer window.
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`: session actions, roster rendering, dealer-side controls.
- `src/Catsino.Plugin/Ui/SessionWindow.cs`: detachable per-session window.

## Runtime Coordination

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`: main runtime state machine and orchestration layer; the payout recovery path lives here (`PollBackendStateAsync`/`SynchronizeAfterHubConnectionAsync` replay the outbox first, `RecoverOpenPayoutAsync` resumes or `ReconcileStrandedOperationAsync` reconciles open operations).
- `src/Catsino.Plugin/Runtime/SessionRosterStore.cs`: roster cache, refresh control, stale-data protection.
- `src/Catsino.Plugin/Runtime/GameChat.cs`: in-game chat command handling for invites.

## Backend Integration

- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`: HTTP surface to backend (includes `ReconcileOperationAsync` for handing a stranded, physically-opened payout to backend reconciliation).
- `src/Catsino.Plugin/Backend/PluginHubClient.cs`: SignalR lifecycle and server-pushed commands.
- `src/Catsino.Plugin/Backend/FinancialIdempotency.cs`: stable financial idempotency handling.

## Payout And Trade Execution

- `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`: payout orchestration and backend event transport; owns the durable-outbox start guard, `ResumeBackendOperationAsync` (recovery resume/reconcile decision, `PayoutResumeOutcome`), the `onLegSettled` signal, and the `MarkOpenEventPersisted` confirm-release.
- `src/Catsino.Plugin/Payout/PayoutExecutionPolicy.cs`: readiness and safety checks before execution.
- `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`: durable event storage and replay; `HasPendingForOperationAsync` is the cross-restart guard that blocks re-trading a leg with undelivered events.
- `src/Catsino.Plugin/Payout/BuiltInPayoutTradeExecutor.cs`: built-in outgoing payout trade executor; `ConfirmTrade` holds off moving gil until `TradeOpened` is durably persisted.
- `src/Catsino.Plugin/Payout/PayoutTradeModels.cs`: structured trade observation and executor event models.

## Security And Config

- `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`: local secret storage.
- `src/Catsino.Plugin/Security/SecretRedactor.cs`: log redaction.
- `src/Catsino.Plugin/Security/DealerInputValidator.cs`: dealer-side validation.
- `src/Catsino.Plugin/Configuration/PluginConfiguration.cs`: persistent local config, including the default dealer fee used for new sessions.

## Contracts And Docs

- `src/Catsino.Plugin.Contracts/DealerContracts.cs`: authorization and dealer DTOs.
- `src/Catsino.Plugin.Contracts/GameSessionContracts.cs`: session and roster DTOs.
- `src/Catsino.Plugin.Contracts/PayoutContracts.cs`: payout DTOs.
- `docs/protocol/backend-v1.md`: backend protocol reference.
