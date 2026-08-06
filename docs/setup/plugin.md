# Plugin Setup

## Requirements

- Windows x64
- .NET SDK 10.0.302 or a compatible later 10.0 feature band
- Dalamud API 15 development files
- A compatible Catsino backend using contract v1.2.0

The plugin contains its own payout trade executor. No separate payout plugin is required on the dealer client.

## Backend URL

The official build connects to `https://152-53-121-56.sslip.io/`. Only use an HTTPS endpoint controlled by Catsino because dealer activation and refresh credentials are sent to it. A custom build can override `ApiBaseUrl` in the generated Dalamud plugin configuration while the plugin is unloaded.

Refresh credentials are protected for the current Windows user with DPAPI. Access tokens and activation JWTs remain in memory and are never intentionally logged. The random `DeviceId` is persisted by Dalamud plugin configuration and is not a secret.

## Local Build

```powershell
dotnet restore Catsino.slnx
dotnet build Catsino.slnx --configuration Release --no-restore
dotnet test Catsino.slnx --configuration Release --no-build
```

The Dalamud SDK writes the packaged plugin to the plugin project's Release output. Do not publish plugin configuration, `credentials.dat`, the `outbox` directory, logs, or build outputs.

## Custom Repository

`repo.json` is the published custom repository manifest. Each release must update its immutable GitHub asset URLs, versions, and changelog, then verify the generated zip checksum from a clean machine.
