# AI Docs Entry Point

Read this file first at the start of every fresh AI session before touching `catsino-dalamud-plugin` code.

Keep `aidocs/` updated in the same change set as the code. If runtime behavior, integration rules, protocol usage, file layout, or operator expectations change, update these docs immediately.

## What This Repo Is

`catsino-dalamud-plugin` is the public, untrusted dealer client for Catsino.

- It runs inside FFXIV through Dalamud.
- It authorizes the dealer and connects outward to the backend.
- It manages sessions, invites, player roster views, dealer actions, payout execution, and — for the turn-based games — the **live dealer table** on the session's **Table** sub-tab (Blackjack: Deal / Hit / Stay; Hold'em: Deal).
- **Three game types** can be created: `plinko`, `blackjack` and `holdem`. Plinko is instant; Blackjack is the turn-based table the dealer plays a hand at; Texas Hold'em is player-versus-player, so the dealer only starts each hand and the backend runs the streets. The game type is chosen in the create-session UI and sent as `CreateGameSessionRequest.GameType`.
- It stores per-plugin defaults (dealer fee, min/max bet, and an optional player cap) that pre-fill newly created sessions. The player cap is sent as `CreateGameSessionRequest.MaxPlayers` (on the wire since public contract 1.4.0; null = unlimited) and is enforced server-side at invite redemption. **Hold'em has no "unlimited"**: its tables seat at most 10 players, so the create-session UI says so, `DealerInputValidator.ValidateMaxPlayers` rejects a larger number up front, and an empty field is sent as a full table (the backend clamps anything larger anyway).
- It sends invite requests with explicit Home World and starting balance, and blocks duplicate invites for active or already-pending players — except the per-player "Reinvite" action, which deliberately re-sends a fresh link to an active player (redeeming it resumes their membership, wallet kept).
- It contains no authoritative balance engine, no backend secrets, no database logic, and no trusted game-outcome logic (neither Plinko results nor any card, hand value, betting rule or pot split — the backend deals every card, values every hand, runs the turn clock, and settles). Every table it renders is a server projection: for Blackjack the dealer's own full hand is shown but the shoe never reaches the plugin; for Hold'em the dealer's view carries **no hole card at all**, not even at showdown, because the dealer plays no hand and withholding them is the only way they cannot leak.
- Outgoing payout execution is built directly into the dealer plugin.

## Read Order

1. `README.md`
2. `src/Catsino.Plugin/Plugin.cs`
3. `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
4. `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
5. `src/Catsino.Plugin/Backend/PluginHubClient.cs`
6. `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`
7. `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`
8. `src/Catsino.Plugin/Ui/CatsinoWindow.cs`
9. `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
10. `docs/protocol/backend-v1.md`

## Repo Map

- `src/Catsino.Plugin`: actual Dalamud plugin runtime, UI, backend client, payout logic, built-in trade executor, security helpers.
- `src/Catsino.Plugin.Contracts`: public backend wire contracts.
- `docs/protocol`: backend protocol reference.
- `docs/setup`: plugin setup notes.
- `tests`: unit and integration coverage around protocol, payout, security, outbox, and trade completion detection.

## First Places To Look

- Plugin entry point: `src/Catsino.Plugin/Plugin.cs`
- Runtime state and coordination: `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- HTTP backend client: `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- SignalR hub client: `src/Catsino.Plugin/Backend/PluginHubClient.cs`
- Payout execution and event transport: `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`
- Durable payout replay: `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`
- Built-in payout executor: `src/Catsino.Plugin/Payout/BuiltInPayoutTradeExecutor.cs`
- Main UI: `src/Catsino.Plugin/Ui/CatsinoWindow.cs`
- Session UI and dealer actions: `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs` (picks the **Table** sub-tab renderer by game type)
- Blackjack table UI: `src/Catsino.Plugin/Ui/BlackjackPanelRenderer.cs`; Hold'em table UI: `src/Catsino.Plugin/Ui/HoldemPanelRenderer.cs`; shared card images: `src/Catsino.Plugin/Ui/CardTextures.cs`
- Live table state (hub push + fast poll): `src/Catsino.Plugin/Runtime/BlackjackTableStore.cs` / `HoldemTableStore.cs` + `RefreshTableGamesAsync` in `CatsinoRuntime.cs`
- Local validation and secret handling: `src/Catsino.Plugin/Security/`

## Key Invariants

- Treat the plugin as untrusted from the backend point of view. (This is the internal trust model — it is **not** the public store description; that copy is the plainer "dealer's control panel, paired with the Catsino backend" in `repo.json` / `Catsino.Plugin.json`.)
- The plugin must not become an authoritative balance or game-outcome source. For Blackjack it renders the server's table projection and submits dealer Deal/Hit/Stay; for Hold'em it renders the projection and submits Deal only. It never deals cards, values hands, checks a bet's legality, or splits a pot locally.
- Contract versions are explicit and exact (public contract **1.8.0**; the backend also accepts **1.7.0** during rollout).
- Sessions carry a per-dealer number (`GameSessionDto.DealerSessionNumber`) shown as `#1`, `#2`, … so the dealer can tell their tables apart. The backend owns it: it is the lowest number that dealer is not already using, and a deleted session's number is reused without renumbering the others.
- Payout execution requires a ready built-in trade executor and exact player identity.
- One active payout operation at a time.
- Durable outbox before backend acknowledgement.
- Ambiguous payout outcomes must not be auto-completed.
- Financial actions use idempotency keys.

## Aidocs Layout

- `high/architecture.md`: high-level structure and trust boundaries.
- `high/runtime-lifecycle.md`: startup, auth restore, polling, hub reconnection, shutdown.
- `mid/common-task-map.md`: where to work for common feature areas.
- `mid/integration-and-payout.md`: backend, hub, trade executor, and outbox integration notes.
- `low/file-map.md`: file-by-file navigation notes.
- `low/test-map.md`: which tests protect which rules.

## Working Rule For Agents

Before making changes, identify whether the work belongs to UI, runtime coordination, backend integration, payout execution, or public contracts.

- UI change: start in `src/Catsino.Plugin/Ui/`.
- Runtime or state change: start in `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`.
- HTTP or hub change: start in `src/Catsino.Plugin/Backend/`.
- Payout or trade-executor change: start in `src/Catsino.Plugin/Payout/`.
- Wire shape change: inspect `src/Catsino.Plugin.Contracts/` plus protocol docs and tests.
