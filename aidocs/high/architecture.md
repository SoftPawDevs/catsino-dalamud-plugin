# Plugin Architecture

## Purpose

This repo is the public dealer client for Catsino running inside FFXIV through Dalamud.

## Project Responsibilities

- `src/Catsino.Plugin`: runtime, UI, backend connectivity, local persistence, payout coordination, Dropbox bridge.
- `src/Catsino.Plugin.Contracts`: public backend request and response DTOs.
- `src/Catsino.Dropbox.Contracts`: payout-only IPC contract and capability definitions.

## Trust Boundary

- The plugin is not trusted for balances, session ownership, payout truth, or Plinko authority.
- The backend remains authoritative.
- The plugin's job is to present state, send requests, execute approved payout legs, and report observed outcomes.

## Main Runtime Surfaces

- Entry point: `src/Catsino.Plugin/Plugin.cs`
- Runtime coordinator: `src/Catsino.Plugin/Runtime/CatsinoRuntime.cs`
- HTTP client: `src/Catsino.Plugin/Backend/CatsinoApiClient.cs`
- SignalR client: `src/Catsino.Plugin/Backend/PluginHubClient.cs`
- Payout engine: `src/Catsino.Plugin/Payout/PayoutCoordinator.cs`
- Local durable outbox: `src/Catsino.Plugin/Payout/PersistentPayoutOutbox.cs`
- Dropbox adapter: `src/Catsino.Plugin/Dropbox/DalamudDropboxPayoutClient.cs`

## Security And Secrets

- Local credential storage: `src/Catsino.Plugin/Security/ProtectedCredentialStore.cs`
- Log redaction: `src/Catsino.Plugin/Security/SecretRedactor.cs`
- Dealer input validation: `src/Catsino.Plugin/Security/DealerInputValidator.cs`

## External References

- Backend protocol: `docs/protocol/backend-v1.md`
- Dropbox IPC protocol: `docs/protocol/dropbox-ipc-v1.md`
- Setup docs: `docs/setup/plugin.md` and `docs/setup/dropbox.md`
