# Catsino Dalamud Plugin

Public, untrusted dealer client for Catsino. It provides dealer authorization, multi-window session roster management, exact-player invites, confirmed signed balance adjustments, and execution of backend-issued outgoing payout legs through a narrowly versioned Dropbox IPC.

The client contains no database access, server secrets, authoritative balances, fee/net/payout calculations, Plinko logic, admin functions, deployment logic, or fallback casino behavior. Dropbox is never used for inbound trades or deposits.

## Projects

- `src/Catsino.Plugin`: Dalamud API 15 plugin targeting `net10.0-windows`.
- `src/Catsino.Plugin.Contracts`: portable public backend contract v1.1.0.
- `src/Catsino.Dropbox.Contracts`: portable payout IPC v1 contract and structured completion detector.
- `tests/Catsino.Plugin.Tests`: client, policy, redaction, idempotency, and durable outbox tests.
- `tests/Catsino.Dropbox.IntegrationTests`: language-independent trade-state tests.

## Commands

```powershell
dotnet restore Catsino.slnx
dotnet build Catsino.slnx --configuration Release --no-restore
dotnet test Catsino.slnx --configuration Release --no-build
```

Open the main plugin window in game with `/catsino`; session panels can be detached into multiple simultaneous windows.

See `docs/setup/plugin.md`, `docs/setup/dropbox.md`, `docs/protocol/backend-v1.md`, and `docs/protocol/dropbox-ipc-v1.md` before packaging a release.
