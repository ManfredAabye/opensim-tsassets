# opensim-tsassets

**TSAssets** (Tiered-Storage Assets) is an OpenSimulator asset service that splits
asset storage by type: every asset type is written to its own MySQL table
(`assets_0`, `assets_1`, `assets_49`, …) and an index table
(`tsassets_index`) is maintained so lookups remain fast regardless of the
number of tables.

Two main components are included:

| Component | Location | Purpose |
|-----------|----------|---------|
| `TSAssetConnector` | `OpenSim/Services/AssetService/` | ROBUST service module – routes each asset type to the correct database connection |
| `MySQLtsAssetData` | `OpenSim/Data/MySQL/` | MySQL data provider – stores/retrieves assets from type-specific tables |

---

## How split assets work

```
Store(asset) ──► TSAssetConnector
                    │
                    ├── ResolveDatabaseForType(asset.Type)
                    │       └── m_typeDatabases[type]  or  m_defaultDatabase
                    │
                    └── MySQLtsAssetData.StoreAsset(asset)
                            │
                            ├── EnsureTypeTable("assets_<type>")   ← created on first write
                            ├── REPLACE INTO `assets_<type>` …
                            └── REPLACE INTO `tsassets_index` …
```

- Each OpenSim asset type (texture = 0, sound = 1, mesh = 49, …) lives in
  its own table, so large tables do not slow down queries for other types.
- A single `tsassets_index` table records `(id, assetType)` to allow fast
  type-directed reads without scanning every typed table.
- An optional **per-type connection string** lets you route specific asset
  types to entirely separate database servers.
- An optional **fallback service** (`FallbackService`) allows reading from the
  original asset database while new assets are stored in TSAssets.  A
  background migration worker can copy fallback assets during low-traffic
  periods.

---

## Requirements

- OpenSimulator 0.9.x / 0.9.3+
- MySQL / MariaDB
- The `OpenSim.Data.MySQL.dll` and `OpenSim.Services.AssetService.dll`
  assemblies from the same OpenSim build

---

## Installation

1. Copy `OpenSim/Services/AssetService/TSAssetConnector.cs` and
   `OpenSim/Data/MySQL/MySQLtsAssetData.cs` into the matching source
   directories of your OpenSim tree and rebuild, **or** place the
   pre-built DLLs in the `bin/` directory of your ROBUST server.

2. Copy `OpenSim/Data/MySQL/Resources/TSAssetStore.migrations` next to the
   other OpenSim migration files so the index table is created automatically
   on first start.

3. Edit `bin/Robust.HG.ini` (see `bin/Robust.HG.ini.example` for a full
   commented example):

```ini
[AssetService]
    LocalServiceModule = "OpenSim.Services.AssetService.dll:TSAssetConnector"

    ; Legacy fallback – assets not yet in TSAssets are read from here
    FallbackService = "OpenSim.Services.AssetService.dll:AssetService"

    ; Provider used by the FallbackService
    StorageProvider = "OpenSim.Data.MySQL.dll:MySQLAssetData"
    ConnectionString = "Source=localhost;Database=robust;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;"

    DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
    AssetLoaderArgs = "./assets/AssetSets.xml"

[TSAssetService]
    StorageProvider = "OpenSim.Data.MySQL.dll:MySQLtsAssetData"
    ConnectionString = "Source=localhost;Database=robust;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;"

    ; Optional: comma/semicolon-separated list of asset types to route.
    ; Leave empty to route all types.
    TSAssetType = ""

    ; Background migration of fallback assets during low traffic
    EnableFallbackAutoMigration = false
    EnableFallbackAutoDelete    = false
    MigrationCheckIntervalSeconds = 60
    MigrationLowTrafficMaxRequests = 3
    MigrationBatchSize  = 25
    MigrationQueueMax   = 50000

    ; Route specific asset types to separate database connections.
    ; Format: <assetType>:<connectionString>;;
    AssetDatabases = "
        0:Source=localhost;Database=robust_textures;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;;
        49:Source=localhost;Database=robust_mesh;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;;
    "
```

---

## OpenSim asset type reference

| Type | Name |
|------|------|
| -2 | LegacyMaterial |
| 0 | Texture |
| 1 | Sound |
| 2 | CallingCard |
| 3 | Landmark |
| 5 | Clothing |
| 6 | Object |
| 7 | Notecard |
| 8 | Folder |
| 10 | Script (LSL text) |
| 11 | LSL bytecode |
| 13 | Bodypart |
| 17 | Animation |
| 18 | Gesture |
| 19 | Simstate |
| 20 | FavoriteFolder |
| 21 | Link |
| 22 | LinkFolder |
| 24 | EnsembleStart |
| 25 | EnsembleEnd |
| 26 | CurrentOutfitFolder |
| 49 | Mesh |
| 56 | Settings |
| 57 | Material |

---

## Database schema

The migration in `OpenSim/Data/MySQL/Resources/TSAssetStore.migrations`
creates the index table on first start:

```sql
CREATE TABLE IF NOT EXISTS `tsassets_index` (
  `id`         char(36)    NOT NULL,
  `assetType`  tinyint(4)  NOT NULL,
  `updated_at` int(11)     NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`),
  INDEX `idx_tsassets_index_assetType` (`assetType`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
```

Type tables (`assets_0`, `assets_1`, …) are created dynamically the first
time an asset of that type is stored:

```sql
CREATE TABLE IF NOT EXISTS `assets_0` (
  `name`        varchar(64)  NOT NULL,
  `description` varchar(64)  NOT NULL,
  `assetType`   tinyint(4)   NOT NULL,
  `local`       tinyint(1)   NOT NULL,
  `temporary`   tinyint(1)   NOT NULL,
  `data`        longblob     NOT NULL,
  `id`          char(36)     NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  `create_time` int(11)      DEFAULT '0',
  `access_time` int(11)      DEFAULT '0',
  `asset_flags` int(11)      NOT NULL DEFAULT '0',
  `CreatorID`   varchar(128) NOT NULL DEFAULT '',
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8;
```

---

## Legacy migration

Set `TSFallbackToLegacyAssets=true` as a parameter in the TSAssetService
`ConnectionString` to automatically migrate assets from the legacy `assets`
table to their typed tables on first read:

```ini
ConnectionString = "…;TSFallbackToLegacyAssets=true"
```

---

## License

BSD 3-Clause – see [LICENSE](LICENSE).