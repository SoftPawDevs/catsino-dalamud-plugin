# Plugin Architecture

## Purpose

This repo is the public dealer client for Catsino running inside FFXIV through Dalamud.

## Project Responsibilities

- `src/Catsino.Plugin`: runtime, UI, backend connectivity, local persistence, payout coordination, and the built-in outgoing trade executor.
- `src/Catsino.Plugin.Contracts`: public backend request and response DTOs.

## Trust Boundary

- The plugin is not trusted for balances, session ownership, payout truth, or game authority (neither Plinko outcomes nor Blackjack cards/hand values).
- The backend remains authoritative.
- The plugin presents state, sends requests, executes approved payout legs, and reports observed outcomes.
- For Blackjack it renders the backend's per-dealer table projection (its own full hand included) and submits dealer Deal/Hit/Stay; the shoe and hand-valuation never live in the plugin.

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
- **State:** `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs` holds the latest `BlackjackTableDto` per session. It is fed two ways: the `PluginHubClient.BlackjackStateChanged` hub event (device-pushed `BlackjackStateChanged`), and a ~2s poll (`RefreshBlackjackTablesAsync` in `CatsinoRuntime.cs`) for tracked blackjack sessions so the controls always reflect whose turn it really is even if a live push was missed.
- **API:** `CatsinoApiClient` exposes `GetBlackjackTableAsync` / `DealBlackjackAsync` / `DealerBlackjackHitAsync` / `DealerBlackjackStayAsync` (all returning the dealer `BlackjackTableDto`; the mutating ones carry an idempotency key).

## Security And Secrets

- Local credential storage: `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`
- Log redaction: `src/Catsino.Plugin/Security/SecretRedactor.cs`
- Dealer input validation: `src/Catsino.Plugin/Security/DealerInputValidator.cs`

## External References

- Backend protocol: `docs/protocol/backend-v1.md`
- Setup docs: `docs/setup/plugin.md`
