# Runtime Lifecycle

## Startup

1. `src/Catsino.Plugin/Plugin.cs` registers `/catsino`, builds windows, and creates `CatsinoRuntime`.
2. `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs` loads configuration, ensures a device id exists, normalizes the persisted default dealer fee, reads current character identity, and creates local services.
3. The runtime initializes credential storage, durable payout outbox, HTTP client, hub client, and payout coordinator.

## Authorization

1. Dealer enters an activation token through the plugin UI.
2. `CatsinoRuntime.AuthorizeAsync` calls `CatsinoApiClient.AuthorizeAsync`.
3. The runtime connects the authorized client to the backend hub and syncs state.
4. If the player is already logged in, authorization restore may run automatically on startup.

## Update Loop

`CatsinoRuntime` uses the framework update hook to drive periodic tasks such as identity checks, heartbeat, session refresh, roster refresh, and recovery behavior. For turn-based sessions it also runs a ~2s live-table poll (`RefreshTableGamesAsync`, dispatching per game type) so the dealer controls always reflect whose turn it is even if a `BlackjackStateChanged` / `HoldemStateChanged` hub push was missed.

## Session Workflow

- Session loading and selection are coordinated in `CatsinoRuntime`.
- The Sessions tab keeps a persisted `Default Dealer Fee %` value that becomes the starting fee for newly created sessions; the create-session form also picks the **game type** (Plinko / Blackjack / Texas Hold'em / Roulette) and shows the Hold'em 10-seat rule next to the player cap. A Blackjack, Hold'em or Roulette session's detail view adds a `Table` sub-tab hosting that game's live dealer table.
- Session windows are opened from `Plugin.cs` and rendered through `Ui/SessionWindow.cs` and `Ui/SessionPanelRenderer.cs`.
- Roster data is cached and refreshed through `Runtime/SessionRosterStore.cs`.

## Invite Workflow

1. UI submits an invite request.
2. Runtime calls the backend create-invite API.
3. The plugin sends the returned message through the native `/tell` path.
4. Pending invites are tracked in runtime state until refreshed or cancelled.

## Payout Workflow (client-driven cash-out)

1. The dealer submits a cash-out; `POST /cashouts` returns the full leg plan (`CashOutResponse`).
2. `CatsinoRuntime.SubmitCashOutAsync` builds a `CashOutBatchPlan` and calls `PayoutBatchCoordinator.StartBatchAsync`,
   which persists the plan durably before any trade.
3. Legs run one at a time through the built-in trade executor; the executor does not move gil until the leg is
   durably marked `Trading` (confirm barrier via `MarkOpenEventPersisted`).
4. On each `TradeCompleted` the coordinator durably records the leg and starts the next pending leg — no backend
   push is involved.
5. When all legs finish (or one fails/is ambiguous) the batch is reported once via `POST /cashouts/{id}/settle`,
   retried durably until acknowledged, then the durable batch is deleted.

## Recovery And Shutdown

- Hub reconnect and terminal disconnect recovery are coordinated in `CatsinoRuntime` and `PluginHubClient`.
- `SynchronizeAfterHubConnectionAsync` (reconnect) and `PollBackendStateAsync` (timer) call
  `PayoutBatchCoordinator.ResumeAsync`, which reloads the durable batch and finishes pending legs, quarantines a
  leg caught mid-trade (as `ambiguous`, never re-traded), or retries a pending settlement. `GET /cashouts/open`
  is a backend cross-check.
- The dealer roster refresh (`RefreshDealerSessions`) is debounced (`RequestDealerRefreshAsync`) so bursts of
  pushes — e.g. rapid Plinko drops — collapse into a single roster re-fetch.
- Plugin disposal unregisters UI hooks, command handlers, and async runtime resources.
