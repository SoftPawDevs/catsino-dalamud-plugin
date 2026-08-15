# Common Task Map

## Authorization And Pairing

- `src/Catsino.Plugin/Ui/CatsinoWindow.cs`
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`
- `tests/Catsino.Plugin.Tests/ValidationAndSecurityTests.cs`

## Session Creation, Lists, Selection, Roster

- `src/Catsino.Plugin/Ui/CatsinoWindow.cs` (create-session form: **game type** selector (Plinko / Blackjack / Texas Hold'em / Roulette), min/max bet taking k/m/b shorthand with the resolved amounts echoed under the row, default fee, min/max bet, and **Max players** — empty = unlimited, except Hold'em where the hint reads "max 10 players", empty means a full table, and a larger number is rejected before the request goes out)
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`CreateSessionAsync(gameType, feePercent, minBet, maxBet, maxPlayers, …)` — sends `CreateGameSessionRequest.GameType`, accepts only `plinko`/`blackjack`/`holdem`/`roulette`, and resolves the cap through `DealerInputValidator.ResolveMaxPlayers`; per-plugin defaults incl. `DefaultMaxPlayers`)
- `src/Catsino.Plugin/Configuration/PluginConfiguration.cs` (`DefaultMaxPlayers`), `src/Catsino.Plugin/Security/DealerInputValidator.cs` (`TryParseMaxPlayers`/`ValidateMaxPlayers`)
- `src/Catsino.Plugin/Runtime/SessionRosterStore.cs`
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (shows `Players: N / cap`)
- `src/Catsino.Plugin/Ui/GameTypeLabels.cs` (the wire's bare `holdem` → `Hold'em`, `roulette` → `Roulette`, and the `#1 Hold'em | Open` session summary; nothing dealer-facing prints a raw game type)
- `tests/Catsino.Plugin.Tests/DealerSessionStateTests.cs`

## Invites And Tell Command Flow

- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- `src/Catsino.Plugin/Runtime/GameChat.cs`
- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`

Notes:
Invite creation now depends on exact `Character Name`, exact `Home World`, and an explicit starting balance. The plugin UI and runtime both reject duplicate invites when the roster already shows the player as active or pending.

**Reinvite** is the deliberate exception: the per-player roster row has a "Reinvite" button (`SessionPanelRenderer.DrawPlayerRow`) that calls `CatsinoRuntime.ReinviteAndTellAsync(sessionId, membershipId, name, world)` → `CatsinoApiClient.ReinviteAsync` (`POST api/v1/game-sessions/{sessionId}/players/{membershipId}/reinvite`). It bypasses the duplicate/active guard on purpose (redeeming resumes the active membership, wallet kept) and `/tell`s the fresh link.

## Blackjack Dealer Table (Deal / Hit / Stay)

- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (the **Table** sub-tab on a blackjack session hosts the panel)
- `src/Catsino.Plugin/Ui/BlackjackPanelRenderer.cs` (dealer hand, seat rows, active-turn highlight + 45s countdown, **Deal / Hit / Stay** — Hit/Stay only enabled when table status is `dealerTurn`)
- `src/Catsino.Plugin/Ui/CardTextures.cs` (card face/back textures from embedded `Assets/Cards/*.png` via `ITextureProvider`)
- `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs` (latest `BlackjackTableDto` per session)
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`RefreshBlackjackTableAsync`; the shared ~2s `RefreshTableGamesAsync` poll dispatches per game type; hub wiring `hub.BlackjackStateChanged += … blackjackStore.Set(table)`)
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs` (`GetBlackjackTableAsync`, `DealBlackjackAsync`, `DealerBlackjackHitAsync`, `DealerBlackjackStayAsync`)
- `src/Catsino.Plugin/Backend/PluginHubProtocol.cs` + `PluginHubClient.cs` (`BlackjackStateChanged` device push)

## Roulette Dealer Table (Spin)

The dealer releases the ball and nothing else. The backend draws the number at the spin and books the
payouts when it lands. Nothing at this table is secret, so the dealer's projection is the players' one.

- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (the **Table** sub-tab on a `roulette` session hosts the panel; the game type -> renderer switch lives here)
- `src/Catsino.Plugin/Ui/RoulettePanelRenderer.cs` (the wheel with the ball in its pocket, every player's chips with the field named in full, last numbers, **Spin** + Refresh)
- `src/Catsino.Plugin/Ui/RouletteTextures.cs` + `src/Catsino.Plugin/Assets/Roulette/*.png` (embedded wheel art)
- `src/Catsino.Plugin/Ui/RouletteSounds.cs` + `Assets/Roulette/*.ogg` (spin / stop clips, NVorbis decode + NAudio WinMM output, muted via `PluginConfiguration.RouletteSoundsEnabled`)
- `src/Catsino.Plugin/Runtime/RouletteTableStore.cs` (latest `RouletteTableDto` per session)
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`SpinRouletteAsync` / `RefreshRouletteTableAsync`; the shared `RefreshTableGamesAsync` poll; hub wiring `hub.RouletteStateChanged += … rouletteStore.Set(table)`)
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs` (`GetRouletteTableAsync`, `SpinRouletteAsync`)
- `src/Catsino.Plugin/Backend/PluginHubProtocol.cs` + `PluginHubClient.cs` (`RouletteStateChanged` device push)

## Settle & Remove (a payout made outside the game)

For a win too large to hand over in 1M trades, the dealer sells to the player on the marketboard and then
books the payout here. The plugin executes no trade on this path.

- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (the **Settle & remove** button next to every Cash out button, and `DrawManualSettlementConfirmation` — gross / dealer fee / **net to pay**, plus the explicit "no trade is executed" note)
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`RequestManualSettlementPreviewAsync` / `SubmitManualSettlementAsync` / `CancelManualSettlement`, and the `manualSettlements` submission map cleaned up alongside the cash-out one)
- `src/Catsino.Plugin/Workflow/DealerSessionActions.cs` (`ManualSettlementSubmission` — the pending/sending/failed state machine with a retained idempotency key)
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs` (`SettleManuallyAsync`)

## Texas Hold'em Dealer Table (Deal)

The dealer plays no hand at a Hold'em table — players play each other for the pot. The only control is
starting the next hand; the backend runs the streets, enforces the betting rules and settles the pots.
**The dealer's projection contains no hole card at all**, even after a showdown.

- `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (the **Table** sub-tab on a `holdem` session hosts the panel; the game type → renderer switch lives here)
- `src/Catsino.Plugin/Ui/HoldemPanelRenderer.cs` (board + pot/side pots, per-seat stack/bet/status with D/SB/BB markers, active-turn highlight + 45s countdown, **Deal** + Refresh; face-down backs stand in for the hole cards the plugin never receives). A finished hand shows its results for ten seconds, then the backend clears the table by itself and **Deal** lights up again.
- `src/Catsino.Plugin/Runtime/HoldemTableStore.cs` (latest `HoldemTableDto` per session)
- `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` (`DealHoldemAsync` / `RefreshHoldemTableAsync`; the shared `RefreshTableGamesAsync` poll; hub wiring `hub.HoldemStateChanged += … holdemStore.Set(table)`)
- `src/Catsino.Plugin/Backend/CatsinoApiClient.cs` (`GetHoldemTableAsync`, `DealHoldemAsync`)
- `src/Catsino.Plugin/Backend/PluginHubProtocol.cs` + `PluginHubClient.cs` (`HoldemStateChanged` device push)
- `src/Catsino.Plugin/Security/DealerInputValidator.cs` (`ValidateMaxPlayers`/`ResolveMaxPlayers` — the 10-seat rule)

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

- `src/Catsino.Plugin.Contracts/` (public contract **1.10.0** — `ContractJson.ContractVersion.Current`; `CreateGameSessionRequest`/`GameSessionDto` carry `MaxPlayers` and `DealerSessionNumber`, and the Blackjack, Hold'em and Roulette table/action DTOs plus `ManualSettlementRequest` live here. Backend accepts `{1.9.0, 1.10.0}` (`Contract.ShippedVersion` / `Contract.Version`). The plugin binary version `PluginVersion.Current` tracks the contract and is currently 1.10.0.)
- `docs/protocol/backend-v1.md` + `docs/protocol/backend-v1.fixture.json`
- `tests/Catsino.Plugin.Tests/ApiProtocolTests.cs`
- `tests/Catsino.Plugin.Tests/ProtocolFixtureTests.cs`, `ContractSerializationTests.cs`
