# TS Asset Service Integration Notes

**Revision:** 0.4.3

**This is highly experimental.**

This document explains how the TS asset changes are integrated into the OpenSim build and deployment flow.

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
ConnectionString = "Data Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;"
DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
AssetLoaderArgs = "./assets/AssetSets.xml"

; Optional legacy fallback
; FallbackService = "OpenSim.Services.AssetService.dll:AssetService"

[TSAssetService]
; Data plugin used by TSAssetConnector
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Data Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;TSAdminBatchSize=2000;TSAdminCommandTimeoutSeconds=600;"

; Optional: explicit type list (sbyte range supports negatives)
; TSAssetType = "-2,-1,0,1,2,3,5,6,7,8,10,13,20,21,22,24,49,56,57"

; Optional: route specific types to other DB connections
; AssetDatabases = "
;   -2:Data Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;;
;   49:Data Source=127.0.0.1;Database=robust;User ID=opensim;Password=opensim123;Old Guids=true;SslMode=None;;
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
- Recommended operational defaults for admin commands: `TSAdminBatchSize=2000`, `TSAdminCommandTimeoutSeconds=600`.

## Recommended activation strategy (safe rollout)

Use a staged activation to avoid accidental full migration load.

### 1) Baseline mode (TS disabled)

Keep standard asset service active:

```ini
[AssetService]
;LocalServiceModule = "OpenSim.Services.AssetService.dll:TSAssetConnector"
LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
StorageProvider = "OpenSim.Data.MySQL.dll:MySQLAssetData"
```

This should be your default before TS tests.

### 2) Controlled TS activation

Switch only the service module first:

```ini
[AssetService]
LocalServiceModule = "OpenSim.Services.AssetService.dll:TSAssetConnector"
StorageProvider = "OpenSim.Data.MySQL.dll:MySQLAssetData"
FallbackService = "OpenSim.Services.AssetService.dll:AssetService"
```

Use conservative TS settings for first rollout:

```ini
[TSAssetService]
StorageProvider = "OpenSim.Data.MySQL.dll:MySQLtsAssetData"
EnableFallbackAutoMigration = false
EnableFallbackAutoDelete = false
MigrationCheckIntervalSeconds = 60
MigrationBatchSize = 25
MigrationLowTrafficMaxRequests = 3
MigrationQueueMax = 50000
```

Why this is optimal for first activation:

- Read misses still resolve through fallback service.
- New TS writes can work without aggressive background migration.
- No automatic deletion of fallback data during validation phase.

### 3) Validation phase

Validate in this order:

1. Service starts cleanly and loads `TSAssetConnector`.
2. New assets are readable after write.
3. Existing legacy assets are still readable via fallback.
4. No unexpected performance spikes on DB.

### 4) Optional migration tuning (after successful validation)

Only after stable tests:

- Set `EnableFallbackAutoMigration = true` if you want retry queue migration in low traffic windows.
- Keep `EnableFallbackAutoDelete = false` initially.
- Enable `EnableFallbackAutoDelete = true` only after confirming data parity and backup policy.

### 5) Immediate rollback path

If behavior is not acceptable, rollback with one change:

```ini
[AssetService]
LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"
```

This disables TS routing immediately without removing TS code.

## Server console help (`help`)

TSAsset commands are registered under the `tsasset` command category.

```text
help tsasset
```

Shows all available TSAsset commands with syntax (`tsmove`, `tsshowmove`, `tsfind`, `tsverify`, `tsreindex`, `tscleanlegacy`).

Also useful:

```text
help
```

Lists all command categories in the current server process.

## Quickstart (60 seconds)

Minimal first validation directly in the `R.O.B.U.S.T.` console:

```text
help tsasset
tsverify assets
tsshowmove assets 7
tsmove assets 7 --force --batch=2000 --timeout=600
tsverify 7
tsverify assets
```

Interpretation:

- `tsverify 7`: after a successful move, it should show `missing-index=0`, `wrong-index-type=0`, `orphan-index=0`.
- `tsverify assets`: `legacy-with-index` should not increase (ideally it decreases).
- To resume an interrupted move, run normally without `--reset`; use `--reset` only to intentionally restart.

### Quickstart (read-only)

Safe read-only check flow (no data changes):

```text
help tsasset
tsverify all
tsshowmove assets 7
tsfind 01234567-89ab-cdef-0123-456789abcdef
tsverify assets
```

## TSAsset server command: `tsmove`

`TSAssetConnector` provides the `tsmove` server command to move TS asset rows between legacy and typed tables.

Syntax:

```text
tsmove <from> <to> --force
```

Optional flags:

```text
--reset            ; clears the current move checkpoint and starts from the beginning
--batch=<n>        ; overrides batch size (e.g. --batch=2000)
--timeout=<sec>    ; overrides SQL command timeout per batch (e.g. --timeout=600)
```

Valid values for `<from>` / `<to>`:

- `assets` (legacy table)
- `<type>` (e.g. `7` => `assets_7`)
- `assets_<type>` (e.g. `assets_7`)

Beispiele:

```text
tsmove assets 7 --force
tsmove 7 assets --force
tsmove assets_7 assets --force
tsmove assets 7 --force --reset
tsmove assets 7 --force --batch=2000 --timeout=600
```

Behavior:

- `tsmove` requires explicit `--force` (or `-f`) to prevent accidental bulk moves.
- `tsmove` is resumable via a persistent checkpoint (`tsassets_move_checkpoint`).
- `--reset` discards the existing checkpoint for that move route.
- `tsmove assets 7` only moves rows with `assetType = 7` from `assets`.
- Rows already present in the destination are also removed from the source.
- After a successful move, `tsassets_index` is updated accordingly (removed when destination is `assets`, set when destination is `assets_<type>`).

Tuning (ConnectionString in the `TSAssetService` block):

```ini
ConnectionString = "...;TSAdminBatchSize=1000;TSAdminCommandTimeoutSeconds=300;"
```

### Dry-run preview: `tsshowmove`

Use this command for a no-write preview:

```text
tsshowmove <from> <to>
```

Beispiel:

```text
tsshowmove assets 7
```

Output includes the same counters as `tsmove` (`candidates`, `inserted`, `already-in-target`, `deleted-from-source`, `index-affected`) but does not write anything to the database.

## Weitere TSAsset Commands

### `tsfind`

Searches an asset across all configured TS asset databases and shows table + index status.

```text
tsfind <asset-id>
```

Beispiel:

```text
tsfind 01234567-89ab-cdef-0123-456789abcdef
```

### `tsverify`

Checks consistency between `assets`/`assets_<type>` and `tsassets_index`.

```text
tsverify [all|assets|<type>|assets_<type>]
```

Beispiele:

```text
tsverify
tsverify all
tsverify 7
tsverify assets
```

Output fields:

- `missing-index`: rows in `assets_<type>` without an index row
- `wrong-index-type`: rows in `assets_<type>` where index `assetType` is wrong
- `orphan-index`: index rows for a type without a matching row in `assets_<type>`
- `legacy-with-index`: rows in `assets` that still have an index row

### `tsreindex`

Batch reindex/cleanup for `tsassets_index` (resumable via checkpoint).

```text
tsreindex [all|assets|<type>|assets_<type>] --force [--reset] [--batch=<n>] [--timeout=<sec>]
```

Beispiele:

```text
tsreindex all --force --batch=2000 --timeout=600
tsreindex 7 --force
tsreindex assets --force
```

### `tscleanlegacy`

Removes index rows that still point to `assets` (legacy).

```text
tscleanlegacy --force [--batch=<n>] [--timeout=<sec>]
```

Beispiel:

```text
tscleanlegacy --force --batch=2000 --timeout=600
```
