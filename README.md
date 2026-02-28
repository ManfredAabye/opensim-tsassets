# TS Asset Service Integration Notes

This document explains how the TS asset changes are integrated into the OpenSim build and deployment flow.

## This is highly experimental.

## Why there is no separate `TSAssetService` project in `prebuild.xml`

`TSAssetConnector` is implemented inside the existing AssetService assembly:

- Source file: `OpenSim/Services/AssetService/TSAssetConnector.cs`
- Existing project in `prebuild.xml`: `OpenSim.Services.AssetService`

Because of that, no additional `<Project name="OpenSim.Services.TSAssetService" ...>` block is required.
The file is compiled automatically as part of `OpenSim.Services.AssetService`.

`OpenSim.Services.FSAssetService` in `prebuild.xml` is a separate, existing module (different assembly), so it has its own project block.

## Files involved in this TS integration

- `OpenSim/Services/AssetService/TSAssetConnector.cs` (service connector/routing)
- `OpenSim/Data/MySQL/MySQLtsAssetData.cs` (MySQL TS asset data plugin)
- `OpenSim/Data/MySQL/Resources/TSAssetStore.migrations` (migration resource)
- `OpenSim/Data/Tests/AssetTests.cs` (TS-related test additions)

## Correct copy behavior in build scripts

If you copy directories with:

```bash
cp -r /opt/opensim-tsassets-sicherung/opensim/bin /opt/opensim/bin
cp -r /opt/opensim-tsassets-sicherung/opensim/OpenSim /opt/opensim/OpenSim
```

you can accidentally create nested directories like `bin/bin` or `OpenSim/OpenSim`.

Use content-copy instead:

```bash
mkdir -p /opt/opensim/bin /opt/opensim/OpenSim
cp -a /opt/opensim-tsassets-sicherung/opensim/bin/. /opt/opensim/bin/
cp -a /opt/opensim-tsassets-sicherung/opensim/OpenSim/. /opt/opensim/OpenSim/
```

This merges file contents into the target folders correctly.

## Practical deployment flow

1. Rebuild OpenSim (`runprebuild.sh`, then `dotnet build`).
2. Run your test cycle on server.
3. Publish only after test success and dev-team approval.

This keeps the TS integration controlled and reproducible.

## Minimal `Robust.HG.ini` example

Use the existing `AssetService` section, but point it to `TSAssetConnector` and add a `TSAssetService` section.

```ini
[AssetService]
LocalServiceModule = "OpenSim.Services.AssetService.dll:TSAssetConnector"
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;"
DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
AssetLoaderArgs = "./assets/AssetSets.xml"

; Optional legacy fallback
; FallbackService = "OpenSim.Services.AssetService.dll:AssetService"

[TSAssetService]
; Data plugin used by TSAssetConnector
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;"

; Optional: explicit type list (sbyte range supports negatives)
; TSAssetType = "-2,-1,0,1,2,3,5,6,7,8,10,13,20,21,22,24,49,56,57"

; Optional: route specific types to other DB connections
; AssetDatabases = "
;   -2:Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;;
;   49:Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;;
; "

; Optional fallback migration behavior
; EnableFallbackAutoMigration = true
; MigrationCheckIntervalSeconds = 60
; MigrationBatchSize = 25
; MigrationLowTrafficMaxRequests = 3
; MigrationQueueMax = 50000
; EnableFallbackAutoDelete = false
```

Notes:

- `TSAssetConnector` is loaded from `OpenSim.Services.AssetService.dll`.
- No separate `TSAssetService` assembly is required in `prebuild.xml`.
- Keep `EnableFallbackAutoDelete = false` until migration behavior is validated on your server.
