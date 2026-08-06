# Catsino Dalamud Plugin

Public, untrusted dealer client for Catsino. It provides dealer authorization, multi-window session roster management, exact-player invites, confirmed signed balance adjustments, and execution of backend-issued outgoing payout legs through the plugin's built-in payout trade executor.

The client contains no database access, server secrets, authoritative balances, fee/net/payout calculations, Plinko logic, admin functions, deployment logic, or fallback casino behavior. Incoming trade acceptance is not part of Catsino.

## Projects

- `src/Catsino.Plugin`: Dalamud API 15 plugin targeting `net10.0-windows`.
- `src/Catsino.Plugin.Contracts`: portable public backend contract v1.2.0.
- `tests/Catsino.Plugin.Tests`: client, policy, payout executor, redaction, idempotency, and durable outbox tests.

## Commands

```powershell
dotnet restore Catsino.slnx
dotnet build Catsino.slnx --configuration Release --no-restore
dotnet test Catsino.slnx --configuration Release --no-build
```

Open the main plugin window in game with `/catsino`; session panels can be detached into multiple simultaneous windows.

See `docs/setup/plugin.md` and `docs/protocol/backend-v1.md` before packaging a release.
