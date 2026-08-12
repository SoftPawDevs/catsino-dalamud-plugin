# File Map

## Entry And UI

- `src/Catsino.Plugin/Plugin.cs`: plugin entry point, command registration, window system integration.
- `src/Catsino.Plugin/Ui/CatsinoWindow.cs`: main dealer window (incl. the create-session form's game-type selector: Plinko / Blackjack).
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`: session actions, roster rendering, dealer-side controls; a blackjack session gets a `Manage` / `Table` sub-tab bar, and the `Table` tab hosts the live blackjack table.
- `src/Catsino.Plugin/Ui/BlackjackPanelRenderer.cs`: the live blackjack table — dealer hand, per-seat tokens/bet/hand/status, active-turn highlight + 45s countdown, and the **Deal / Hit / Stay** controls (Hit/Stay enabled only on table status `dealerTurn`).
- `src/Catsino.Plugin/Ui/CardTextures.cs`: card face/back textures loaded from embedded `Assets/Cards/*.png` via `ITextureProvider.GetFromManifestResource` (returns an `ImTextureID`).
- `src/Catsino.Plugin/Ui/SessionWindow.cs`: detachable per-session window.

## Runtime Coordination

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`: main runtime state machine and orchestration layer; the payout recovery path lives here (`PollBackendStateAsync`/`SynchronizeAfterHubConnectionAsync` replay the outbox first, `RecoverOpenPayoutAsync` resumes or `ReconcileStrandedOperationAsync` reconciles open operations).
- `src/Catsino.Plugin/Runtime/SessionRosterStore.cs`: roster cache, refresh control, stale-data protection.
- `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs`: latest `BlackjackTableDto` per session, fed by the hub push and the ~2s poll.
- `src/Catsino.Plugin/Runtime/GameChat.cs`: in-game chat command handling for invites.

Blackjack table refresh (`RefreshBlackjackTableAsync` / `RefreshBlackjackTablesAsync`) and the create-session entry (`CreateSessionAsync(gameType, …)`) both live in `CatsinoRuntime.cs`.

## Backend Integration

- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`: HTTP surface to backend (includes `ReconcileOperationAsync` for handing a stranded, physically-opened payout to backend reconciliation, and the Blackjack dealer calls `GetBlackjackTableAsync`/`DealBlackjackAsync`/`DealerBlackjackHitAsync`/`DealerBlackjackStayAsync`).
- `src/Catsino.Plugin/Backend/PluginHubClient.cs`: SignalR lifecycle and server-pushed commands (incl. the `BlackjackStateChanged` device push).
- `src/Catsino.Plugin/Backend/PluginHubProtocol.cs`: hub event-name constants (incl. `BlackjackStateChanged`).
- `src/Catsino.Plugin/Backend/FinancialIdempotency.cs`: stable financial idempotency handling.

## Payout And Trade Execution

- `src/Catsino.Plugin/Payout/PayoutBatchCoordinator.cs`: client-driven cash-out orchestration — runs the whole batch's legs locally and sequentially, keeps the durable batch as crash-safe truth (quarantine a mid-trade leg, never re-run a completed one), and settles once via `SettleCashOutAsync`.
- `src/Catsino.Plugin/Payout/CashOutBatch.cs`: batch plan/state models, `PersistentCashOutBatchStore` (durable atomic per-batch storage), and `BackendPayoutSettlementTransport`.
- `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`: legacy per-leg push coordinator (durable-outbox start guard, `ResumeBackendOperationAsync`, `MarkOpenEventPersisted`); no longer wired into the runtime's cash-out path.
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
- `src/Catsino.Plugin.Contracts/GameSessionContracts.cs`: session and roster DTOs, plus the Blackjack table/action DTOs (`BlackjackTableDto`, `BlackjackDealRequest`, `BlackjackDealerActionRequest`).
- `src/Catsino.Plugin.Contracts/PayoutContracts.cs`: payout DTOs.
- `src/Catsino.Plugin.Contracts/ContractJson.cs`: `ContractVersion.Current = "1.5.0"` + shared JSON options.
- `src/Catsino.Plugin/Assets/Cards/`: embedded card face + back PNGs used by `CardTextures`.
- `docs/protocol/backend-v1.md`: backend protocol reference.
