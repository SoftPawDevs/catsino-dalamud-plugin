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

`CatsinoRuntime` uses the framework update hook to drive periodic tasks such as identity checks, heartbeat, session refresh, roster refresh, and recovery behavior.

## Session Workflow

- Session loading and selection are coordinated in `CatsinoRuntime`.
- The Sessions tab keeps a persisted `Default Dealer Fee %` value that becomes the starting fee for newly created Plinko sessions.
- Session windows are opened from `Plugin.cs` and rendered through `Ui/SessionWindow.cs` and `Ui/SessionPanelRenderer.cs`.
- Roster data is cached and refreshed through `Runtime/SessionRosterStore.cs`.

## Invite Workflow

1. UI submits an invite request.
2. Runtime calls the backend create-invite API.
3. The plugin sends the returned message through the native `/tell` path.
4. Pending invites are tracked in runtime state until refreshed or cancelled.

## Payout Workflow

1. Backend hub sends a payout leg (`QueuePayoutLeg`).
2. `PayoutCoordinator.StartBackendLegAsync` validates readiness/policy and — unless the durable outbox still
   holds an unsent event for that operation — starts the built-in trade executor.
3. The executor does not move gil until its `TradeOpened` event is durably persisted (confirm barrier).
4. Observed payout events are written to the durable outbox first.
5. Events are sent to the backend and removed only after acknowledgement.
6. On the current leg settling, the next leg starts (via the push, the poll, or the `onLegSettled` callback).

## Recovery And Shutdown

- Hub reconnect and terminal disconnect recovery are coordinated in `CatsinoRuntime` and `PluginHubClient`.
- `SynchronizeAfterHubConnectionAsync` (reconnect) and `PollBackendStateAsync` (timer) both **replay the
  durable outbox first**, then run `RecoverOpenPayoutAsync` over `GET /payout-operations/open`.
- `RecoverOpenPayoutAsync` re-attaches to a leg the executor is already driving, starts a never-begun
  `Queued` / `WaitingForPlayer` leg, or — for a physically-opened leg the executor can no longer drive —
  calls the backend reconcile endpoint (`ReconcileStrandedOperationAsync`) instead of re-trading it.
- Plugin disposal unregisters UI hooks, command handlers, and async runtime resources.
