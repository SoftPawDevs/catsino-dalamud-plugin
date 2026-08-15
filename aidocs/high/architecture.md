# Plugin Architecture

## Purpose

This repo is the public dealer client for Catsino running inside FFXIV through Dalamud.

## Project Responsibilities

- `src/Catsino.Plugin`: runtime, UI, backend connectivity, local persistence, payout coordination, and the built-in outgoing trade executor.
- `src/Catsino.Plugin.Contracts`: public backend request and response DTOs.

## Trust Boundary

- The plugin is not trusted for balances, session ownership, payout truth, or game authority (neither Plinko outcomes nor any card, hand value, betting rule or pot split).
- The backend remains authoritative.
- The plugin presents state, sends requests, executes approved payout legs, and reports observed outcomes.
- For Blackjack it renders the backend's per-dealer table projection (its own full hand included) and submits dealer Deal/Hit/Stay; for Hold'em it renders a projection with no hole cards at all and submits Deal only. The deck/shoe, hand valuation, betting legality and pot splitting never live in the plugin.

## Main Runtime Surfaces

- Entry point: `src/Catsino.Plugin/Plugin.cs`
- Runtime coordinator: `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- HTTP client: `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- SignalR client: `src/Catsino.Plugin/Backend/PluginHubClient.cs`
- Payout engine: `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`
- Built-in trade executor: `src/Catsino.Plugin/Payout/BuiltInPayoutTradeExecutor.cs`
- Local durable outbox: `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`

## Blackjack Dealer Surface

- **UI:** `src/Catsino.Plugin/Ui/BlackjackPanelRenderer.cs` renders the live table on the session's **Table** sub-tab (hosted by `SessionPanelRenderer`): the dealer hand, each seat's tokens/bet/hand/status, the active turn + 45s countdown, and the **Deal / Hit / Stay** controls (Hit/Stay only enabled on the dealer's own turn, i.e. table status `dealerTurn`). Card faces are drawn by `src/Catsino.Plugin/Ui/CardTextures.cs` from PNGs embedded under `Assets/Cards/` (via `ITextureProvider.GetFromManifestResource`).
- **State:** `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs` holds the latest `BlackjackTableDto` per session. It is fed two ways: the `PluginHubClient.BlackjackStateChanged` hub event (device-pushed `BlackjackStateChanged`), and a ~2s poll (`RefreshTableGamesAsync` in `CatsinoRuntime.cs`, which dispatches per game type) so the controls always reflect whose turn it really is even if a live push was missed.
- **API:** `CatsinoApiClient` exposes `GetBlackjackTableAsync` / `DealBlackjackAsync` / `DealerBlackjackHitAsync` / `DealerBlackjackStayAsync` (all returning the dealer `BlackjackTableDto`; the mutating ones carry an idempotency key).

## Texas Hold'em Dealer Surface

Hold'em is player-versus-player: the dealer starts hands and nothing else. The backend deals, runs every
betting street, enforces the rules and settles the pots (zero-sum, no rake — the house income stays the
cash-out fee).

- **UI:** `src/Catsino.Plugin/Ui/HoldemPanelRenderer.cs` renders the live table on the session's **Table**
  sub-tab: the board and pot (with side pots listed when they exist), each seat's stack / current bet / total
  in the pot / status with D-SB-BB markers, the active turn + 45s countdown, and the single **Deal** control.
- **No hole cards, by construction.** The dealer audience's `HoldemTableDto` never carries a hole card — not
  during the hand and not at showdown. The renderer draws face-down backs instead. The dealer plays no hand,
  so they never need one, and withholding it is the only way it cannot be leaked to a friend at the table.
- **State:** `src/Catsino.Plugin/Runtime/HoldemTableStore.cs`, fed by the `HoldemStateChanged` hub push and
  the same ~2s poll.
- **API:** `CatsinoApiClient.GetHoldemTableAsync` / `DealHoldemAsync` (the latter carries an idempotency key).
- **Seat cap:** Hold'em tables seat at most 10 players (`HoldemBetDefaults.MaxSeats`). The create-session UI
  says so, `DealerInputValidator.ValidateMaxPlayers` rejects a larger number, and `ResolveMaxPlayers` sends a
  full table when the field is left empty — Hold'em has no "unlimited".

## Security And Secrets

- Local credential storage: `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`
- Log redaction: `src/Catsino.Plugin/Security/SecretRedactor.cs`
- Dealer input validation: `src/Catsino.Plugin/Security/DealerInputValidator.cs`

## External References

- Backend protocol: `docs/protocol/backend-v1.md`
- Setup docs: `docs/setup/plugin.md`
