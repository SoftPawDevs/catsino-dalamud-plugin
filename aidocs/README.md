# AI Docs Entry Point

Read this file first at the start of every fresh AI session before touching `catsino-dalamud-plugin` code.

Keep `aidocs/` updated in the same change set as the code. If runtime behavior, integration rules, protocol usage, file layout, or operator expectations change, update these docs immediately.

## What This Repo Is

`catsino-dalamud-plugin` is the public, untrusted dealer client for Catsino.

- It runs inside FFXIV through Dalamud.
- It authorizes the dealer and connects outward to the backend.
- It manages sessions, invites, player roster views, dealer actions, and payout execution.
- It stores per-plugin defaults (dealer fee, min/max bet, and an optional player cap) that pre-fill newly created sessions. The player cap is sent as `CreateGameSessionRequest.MaxPlayers` (public contract 1.4.0; null = unlimited) and is enforced server-side at invite redemption.
- It sends invite requests with explicit Home World and starting balance, and blocks duplicate invites for active or already-pending players — except the per-player "Reinvite" action, which deliberately re-sends a fresh link to an active player (redeeming it resumes their membership, wallet kept).
- It contains no authoritative balance engine, no backend secrets, no database logic, and no trusted Plinko outcome logic.
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
- Session UI and dealer actions: `src/Catsino.Plugin/Ui/SessionPanelRenderer.cs`
- Local validation and secret handling: `src/Catsino.Plugin/Security/`

## Key Invariants

- Treat the plugin as untrusted from the backend point of view.
- The plugin must not become an authoritative balance source.
- Contract versions are explicit and exact.
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
