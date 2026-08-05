# Plugin Setup

## Requirements

- Windows x64
- .NET SDK 10.0.302 or a compatible later 10.0 feature band
- Dalamud API 15 development files
- A compatible Catsino backend using contract v1.0.0
- The supported Catsino Dropbox fork for payouts

## Backend URL

The repository intentionally defaults `ApiBaseUrl` to `https://api.catsino.invalid/` because no production endpoint belongs in public source by assumption. Set `ApiBaseUrl` in the generated Dalamud plugin configuration while the plugin is unloaded, or replace the default as part of an official release build. Only use an HTTPS endpoint controlled by Catsino because dealer activation and refresh credentials are sent to it.

Refresh credentials are protected for the current Windows user with DPAPI. Access tokens and activation JWTs remain in memory and are never intentionally logged. The random `DeviceId` is persisted by Dalamud plugin configuration and is not a secret.

## Local Build

```powershell
dotnet restore Catsino.slnx
dotnet build Catsino.slnx --configuration Release --no-restore
dotnet test Catsino.slnx --configuration Release --no-build
```

The Dalamud SDK writes the packaged plugin to the plugin project's Release output. Do not publish plugin configuration, `credentials.dat`, the `outbox` directory, logs, or build outputs.

## Custom Repository

`repo.json` is the custom repository manifest template. Before publishing, replace all `.invalid` release and icon URLs, update versions and changelog, build Release, publish the generated zip, and verify its checksum from a clean machine. Do not publish a manifest that points at mutable artifacts.
