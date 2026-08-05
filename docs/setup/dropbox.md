# Dropbox Fork Setup

The authorized fork starts from `012f11da49d3e5b1f31599111e84631819a52940`. Initialize its pinned submodules before attempting a legacy build:

```powershell
git submodule update --init --recursive
dotnet restore Dropbox.sln
dotnet build Dropbox.sln --configuration Release --no-restore
```

The supported Catsino compatibility build is pinned to this immutable fork commit:

`SUPPORTED_DROPBOX_COMMIT=d2ee0bd2813b1551da8c7871cf74e8f753ba3054`

The public client additionally enforces IPC `1.0.0`, build `1.0.0.7-catsino.1`, all required capabilities, and language-independent trade state. A release that changes the Dropbox assembly build identifier must update both matching public contract projects and their tests.

The fork plugin targets `Dalamud.NET.Sdk/15.0.0` and `net10.0-windows`. It uses the pinned compatible ECommons submodule and no longer builds against ClickLib; current ECommons structured button helpers replace the obsolete click dependency. The full solution, not only the contract project, must pass the Release build before publishing.
