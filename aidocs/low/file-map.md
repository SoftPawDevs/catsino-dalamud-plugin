# File Map

## Entry And UI

- `src/Catsino.Plugin/Plugin.cs`: plugin entry point, command registration, window system integration.
- `src/Catsino.Plugin/Ui/CatsinoWindow.cs`: main dealer window (incl. the create-session form's game-type selector: Plinko / Blackjack / Texas Hold'em / Roulette, the Hold'em 10-seat hint + validation, and the roulette wheel/limits hint).
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`: session actions, roster rendering, dealer-side controls; a table-game session gets a `Manage` / `Table` sub-tab bar, and the `Table` tab hosts that game's live table (Blackjack, Hold'em or Roulette). The roster row also carries **Settle & remove** next to Cash out, whose confirmation window shows gross / dealer fee / net to pay for a payout made outside the game.
- `src/Catsino.Plugin/Ui/RoulettePanelRenderer.cs`: the roulette dealer table — the wheel with the ball in its pocket (phase taken from the round's `DeadlineAt`), every player's chips grouped by player with the field named in full, the last numbers, and the **Spin** control.
- `src/Catsino.Plugin/Ui/RouletteTextures.cs`: resolves the embedded wheel art (`Assets/Roulette/*.png`) to ImGui texture handles.
- `src/Catsino.Plugin/Ui/RouletteSounds.cs`: the spin / stop clips (`Assets/Roulette/*.ogg`), decoded once with NVorbis into memory and played through NAudio WinMM. Decoding up front keeps the ImGui draw loop free of Vorbis work; `PlaySpin(fromSeconds)` lets a panel opened mid-spin join the sound where the wheel already is.
- `src/Catsino.Plugin/Runtime/RouletteTableStore.cs`: latest `RouletteTableDto` per session, newest-wins by `ObservedAt`.
- `src/Catsino.Plugin/Ui/BlackjackPanelRenderer.cs`: the live blackjack table — dealer hand, per-seat tokens/bet/hand/status, active-turn highlight + 45s countdown, and the **Deal / Hit / Stay** controls (Hit/Stay enabled only on table status `dealerTurn`).
- `src/Catsino.Plugin/Ui/CardTextures.cs`: card face/back textures loaded from embedded `Assets/Cards/*.png` via `ITextureProvider.GetFromManifestResource` (returns an `ImTextureID`).
- `src/Catsino.Plugin/Ui/SessionWindow.cs`: detachable per-session window.

## Runtime Coordination

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`: main runtime state machine and orchestration layer; the payout recovery path lives here (`PollBackendStateAsync`/`SynchronizeAfterHubConnectionAsync` replay the outbox first, `RecoverOpenPayoutAsync` resumes or `ReconcileStrandedOperationAsync` reconciles open operations).
- `src/Catsino.Plugin/Runtime/SessionRosterStore.cs`: roster cache, refresh control, stale-data protection.
- `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs` / `HoldemTableStore.cs`: latest table DTO per session, fed by the hub push and the ~2s poll; a snapshot is only replaced by a strictly newer `ObservedAt`.
- `src/Catsino.Plugin/Runtime/GameChat.cs`: in-game chat command handling for invites.

Table refresh (`RefreshBlackjackTableAsync` / `RefreshHoldemTableAsync` / the shared `RefreshTableGamesAsync` poll) and the create-session entry (`CreateSessionAsync(gameType, …)`) all live in `CatsinoRuntime.cs`.

## Backend Integration

- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`: HTTP surface to backend (includes `ReconcileOperationAsync` for handing a stranded, physically-opened payout to backend reconciliation, and the Blackjack dealer calls `GetBlackjackTableAsync`/`DealBlackjackAsync`/`DealerBlackjackHitAsync`/`DealerBlackjackStayAsync`).
- `src/Catsino.Plugin/Backend/PluginHubClient.cs`: SignalR lifecycle and server-pushed commands (incl. the `BlackjackStateChanged` and `HoldemStateChanged` device pushes).
- `src/Catsino.Plugin/Backend/PluginHubProtocol.cs`: hub event-name constants (incl. `BlackjackStateChanged` and `HoldemStateChanged`).
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

- `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`: local secret storage (DPAPI, per Windows user). Kept across a game logout; cleared on character change, deliberate disconnect, or a backend-rejected refresh.
- `src/Catsino.Plugin/Security/SecretRedactor.cs`: log redaction.
- `src/Catsino.Plugin/Security/DealerInputValidator.cs`: dealer-side validation, including the gil shorthand reader (`TryParseShorthandGil`) behind both money boxes — `TryParseBalanceAdjustment` (signed, non-zero) and `TryParseGilAmount` (zero or positive, used for the invite balance). Both accept "250k" / "2.5m" / "1.500.000", so neither field asks the dealer to type zeros the other one would have expanded.
- `src/Catsino.Plugin/Configuration/PluginConfiguration.cs`: persistent local config, including the default dealer fee used for new sessions.

## Contracts And Docs

- `src/Catsino.Plugin.Contracts/DealerContracts.cs`: authorization and dealer DTOs.
- `src/Catsino.Plugin.Contracts/GameSessionContracts.cs`: session and roster DTOs, plus the Blackjack table/action DTOs (`BlackjackTableDto`, `BlackjackDealRequest`, `BlackjackDealerActionRequest`) the Hold'em ones (`HoldemTableDto`, `HoldemSeatDto`, `HoldemPotDto`, `HoldemDealRequest`, `HoldemBetDefaults`), the Roulette ones (`RouletteTableDto`, `RouletteBetDto`, `RouletteSpinRequest`, `RouletteBetDefaults`), and `ManualSettlementRequest`.
- `src/Catsino.Plugin.Contracts/PayoutContracts.cs`: payout DTOs.
- `src/Catsino.Plugin.Contracts/ContractJson.cs`: `ContractVersion.Current = "1.5.0"` + shared JSON options.
- `src/Catsino.Plugin/Assets/Cards/`: embedded card face + back PNGs used by `CardTextures`.
- `docs/protocol/backend-v1.md`: backend protocol reference.
