/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.AssetService
{
    public class TSAssetConnector : ServiceBase, IAssetService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Dictionary<sbyte, IAssetDataPlugin> m_typeDatabases = new Dictionary<sbyte, IAssetDataPlugin>();
        private readonly Dictionary<string, IAssetDataPlugin> m_connectionDatabases = new Dictionary<string, IAssetDataPlugin>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<UUID, IAssetDataPlugin> m_assetLocationCache = new Dictionary<UUID, IAssetDataPlugin>();
        private readonly ConcurrentQueue<AssetBase> m_fallbackMigrationQueue = new ConcurrentQueue<AssetBase>();
        private readonly ConcurrentDictionary<UUID, byte> m_fallbackMigrationSet = new ConcurrentDictionary<UUID, byte>();
        private readonly object m_cacheLock = new object();
        private readonly HashSet<sbyte> m_allowedTypes = new HashSet<sbyte>();

        private readonly List<IAssetDataPlugin> m_probeOrder = new List<IAssetDataPlugin>();

        private IAssetDataPlugin m_defaultDatabase;
        private IAssetLoader m_assetLoader;
        private IAssetService m_fallbackService;
        private Thread m_fallbackMigrationThread;

        private bool m_enableFallbackAutoMigration;
        private bool m_enableFallbackAutoDelete;
        private int m_migrationCheckIntervalMs = 60000;
        private int m_migrationBatchSize = 25;
        private int m_migrationLowTrafficMaxRequests = 3;
        private int m_migrationQueueMax = 50000;
        private int m_requestsInWindow;

        public TSAssetConnector(IConfigSource config)
            : base(config)
        {
            IConfig assetConfig = config.Configs["AssetService"];
            if (assetConfig == null)
                throw new Exception("No AssetService configuration");

            IConfig tsConfig = config.Configs["TSAssetService"];
            IConfig dbConfig = config.Configs["DatabaseService"];

            string storageProvider = string.Empty;
            string defaultConnectionString = string.Empty;

            if (tsConfig != null)
            {
                storageProvider = tsConfig.GetString("StorageProvider", string.Empty);
                defaultConnectionString = tsConfig.GetString("ConnectionString", string.Empty);
            }

            if (string.IsNullOrEmpty(storageProvider))
                storageProvider = assetConfig.GetString("StorageProvider", string.Empty);

            if (string.IsNullOrEmpty(defaultConnectionString))
                defaultConnectionString = assetConfig.GetString("ConnectionString", string.Empty);

            if (dbConfig != null)
            {
                if (string.IsNullOrEmpty(storageProvider))
                    storageProvider = dbConfig.GetString("StorageProvider", string.Empty);

                if (string.IsNullOrEmpty(defaultConnectionString))
                    defaultConnectionString = dbConfig.GetString("ConnectionString", string.Empty);
            }

            if (string.IsNullOrEmpty(storageProvider))
                throw new Exception("No StorageProvider configured");

            if (string.IsNullOrEmpty(defaultConnectionString))
                throw new Exception("Missing database connection string");

            ParseAllowedTypes(tsConfig);

            if (tsConfig != null)
            {
                m_enableFallbackAutoMigration = tsConfig.GetBoolean("EnableFallbackAutoMigration", false);
                m_enableFallbackAutoDelete = tsConfig.GetBoolean("EnableFallbackAutoDelete", false);
                m_migrationCheckIntervalMs = Math.Max(5000, tsConfig.GetInt("MigrationCheckIntervalSeconds", 60) * 1000);
                m_migrationBatchSize = Math.Max(1, tsConfig.GetInt("MigrationBatchSize", 25));
                m_migrationLowTrafficMaxRequests = Math.Max(0, tsConfig.GetInt("MigrationLowTrafficMaxRequests", 3));
                m_migrationQueueMax = Math.Max(100, tsConfig.GetInt("MigrationQueueMax", 50000));
            }

            m_defaultDatabase = CreateAndInitDatabase(storageProvider, defaultConnectionString);
            AddProbeDatabase(m_defaultDatabase);

            if (tsConfig != null)
                ParseTypedDatabaseMappings(tsConfig.GetString("AssetDatabases", string.Empty), storageProvider);

            string loaderName = assetConfig.GetString("DefaultAssetLoader", string.Empty);
            if (!string.IsNullOrEmpty(loaderName))
            {
                m_assetLoader = LoadPlugin<IAssetLoader>(loaderName);
                if (m_assetLoader == null)
                    throw new Exception(string.Format("Asset loader could not be loaded from {0}", loaderName));

                bool assetLoaderEnabled = assetConfig.GetBoolean("AssetLoaderEnabled", true);
                if (assetLoaderEnabled)
                {
                    string loaderArgs = assetConfig.GetString("AssetLoaderArgs", string.Empty);
                    m_log.InfoFormat("[TSASSET SERVICE]: Loading default asset set from {0}", loaderArgs);

                    m_assetLoader.ForEachDefaultXmlAsset(
                        loaderArgs,
                        delegate(AssetBase a)
                        {
                            if (a == null)
                                return;

                            if (Get(a.ID) == null)
                                Store(a);
                        });
                }
            }

            string fallbackServiceName = assetConfig.GetString("FallbackService", string.Empty);
            if (!string.IsNullOrEmpty(fallbackServiceName))
            {
                object[] args = new object[] { config };
                m_fallbackService = LoadPlugin<IAssetService>(fallbackServiceName, args);
                if (m_fallbackService != null)
                {
                    m_log.Info("[TSASSET SERVICE]: Fallback service loaded");

                    if (m_enableFallbackAutoMigration)
                    {
                        m_fallbackMigrationThread = new Thread(FallbackMigrationWorker);
                        m_fallbackMigrationThread.IsBackground = true;
                        m_fallbackMigrationThread.Name = "TSAssetFallbackMigration";
                        m_fallbackMigrationThread.Start();
                        m_log.Info("[TSASSET SERVICE]: Fallback auto-migration worker enabled");
                    }
                }
                else
                    m_log.Error("[TSASSET SERVICE]: Failed to load fallback service");
            }

            m_log.InfoFormat("[TSASSET SERVICE]: Enabled with {0} type routes", m_typeDatabases.Count);
        }

        public AssetBase Get(string id)
        {
            Interlocked.Increment(ref m_requestsInWindow);

            if (!UUID.TryParse(id, out UUID assetID))
            {
                m_log.WarnFormat("[TSASSET SERVICE]: Could not parse requested asset id {0}", id);
                return null;
            }

            IAssetDataPlugin cachedDatabase = null;
            lock (m_cacheLock)
            {
                m_assetLocationCache.TryGetValue(assetID, out cachedDatabase);
            }

            if (cachedDatabase != null)
            {
                try
                {
                    AssetBase cachedAsset = cachedDatabase.GetAsset(assetID);
                    if (cachedAsset != null)
                        return cachedAsset;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Cached database lookup failed for asset {0}: {1}", assetID, e.Message);
                }
            }

            for (int i = 0; i < m_probeOrder.Count; i++)
            {
                IAssetDataPlugin db = m_probeOrder[i];

                try
                {
                    AssetBase asset = db.GetAsset(assetID);
                    if (asset != null)
                    {
                        lock (m_cacheLock)
                        {
                            m_assetLocationCache[assetID] = db;
                        }
                        return asset;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Asset lookup failed for asset {0}: {1}", assetID, e.Message);
                }
            }

            if (m_fallbackService != null)
            {
                try
                {
                    AssetBase fallbackAsset = m_fallbackService.Get(id);
                    if (fallbackAsset != null)
                    {
                        string storedId = Store(fallbackAsset);
                        bool storeOk = !storedId.Equals(UUID.Zero.ToString(), StringComparison.Ordinal);

                        if (storeOk && m_enableFallbackAutoDelete)
                            TryDeleteFromFallback(id);

                        if (m_enableFallbackAutoMigration && !storeOk)
                            EnqueueFallbackMigration(fallbackAsset);

                        return fallbackAsset;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Fallback lookup failed for asset {0}: {1}", id, e.Message);
                }
            }

            return null;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool StoreOnLocalGrid)
        {
            return Get(id);
        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = Get(id);
            return asset != null ? asset.Metadata : null;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = Get(id);
            return asset != null ? asset.Data : null;
        }

        public AssetBase GetCached(string id)
        {
            return Get(id);
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            handler(id, sender, Get(id));
            return true;
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            callBack(Get(id));
        }

        public bool[] AssetsExist(string[] ids)
        {
            if (ids == null || ids.Length == 0)
                return new bool[0];

            bool[] results = new bool[ids.Length];
            UUID[] uuids = new UUID[ids.Length];
            bool[] valid = new bool[ids.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                if (UUID.TryParse(ids[i], out UUID parsed))
                {
                    uuids[i] = parsed;
                    valid[i] = true;
                }
            }

            for (int dbIndex = 0; dbIndex < m_probeOrder.Count; dbIndex++)
            {
                IAssetDataPlugin db = m_probeOrder[dbIndex];
                UUID[] query = BuildPendingUuidList(uuids, valid, results);

                if (query.Length == 0)
                    break;

                try
                {
                    bool[] exists = db.AssetsExist(query);
                    MergeExistenceResults(query, exists, uuids, valid, results, db);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: AssetsExist failed on a database: {0}", e.Message);
                }
            }

            return results;
        }

        public string Store(AssetBase asset)
        {
            if (asset == null)
                return UUID.Zero.ToString();

            IAssetDataPlugin db = ResolveDatabaseForType(asset.Type);
            if (db == null)
            {
                m_log.WarnFormat("[TSASSET SERVICE]: No database resolved for asset {0}, type {1}", asset.FullID, asset.Type);
                return UUID.Zero.ToString();
            }

            try
            {
                bool stored = db.StoreAsset(asset);
                if (!stored)
                    return UUID.Zero.ToString();

                lock (m_cacheLock)
                {
                    m_assetLocationCache[asset.FullID] = db;
                }

                return asset.ID;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[TSASSET SERVICE]: Error storing asset {0}: {1}", asset.FullID, e.Message);
                return UUID.Zero.ToString();
            }
        }

        public bool UpdateContent(string id, byte[] data)
        {
            AssetBase asset = Get(id);
            if (asset == null)
                return false;

            asset.Data = data;
            return Store(asset) != UUID.Zero.ToString();
        }

        public bool Delete(string id)
        {
            if (!UUID.TryParse(id, out UUID assetID))
                return false;

            IAssetDataPlugin cachedDatabase = null;
            lock (m_cacheLock)
            {
                m_assetLocationCache.TryGetValue(assetID, out cachedDatabase);
            }

            if (cachedDatabase != null)
            {
                try
                {
                    bool deleted = cachedDatabase.Delete(id);
                    if (deleted)
                    {
                        lock (m_cacheLock)
                        {
                            m_assetLocationCache.Remove(assetID);
                        }
                        return true;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Cached delete failed for asset {0}: {1}", assetID, e.Message);
                }
            }

            for (int i = 0; i < m_probeOrder.Count; i++)
            {
                try
                {
                    if (m_probeOrder[i].Delete(id))
                    {
                        lock (m_cacheLock)
                        {
                            m_assetLocationCache.Remove(assetID);
                        }
                        return true;
                    }
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Delete failed for asset {0}: {1}", assetID, e.Message);
                }
            }

            return false;
        }

        private static UUID[] BuildPendingUuidList(UUID[] uuids, bool[] valid, bool[] results)
        {
            List<UUID> pending = new List<UUID>(uuids.Length);
            for (int i = 0; i < uuids.Length; i++)
            {
                if (valid[i] && !results[i])
                    pending.Add(uuids[i]);
            }

            return pending.ToArray();
        }

        private void MergeExistenceResults(UUID[] query, bool[] exists, UUID[] sourceIds, bool[] valid, bool[] results, IAssetDataPlugin db)
        {
            if (exists == null)
                return;

            int len = Math.Min(query.Length, exists.Length);
            for (int i = 0; i < len; i++)
            {
                if (!exists[i])
                    continue;

                UUID foundId = query[i];

                for (int sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
                {
                    if (valid[sourceIndex] && sourceIds[sourceIndex] == foundId)
                    {
                        results[sourceIndex] = true;
                    }
                }

                lock (m_cacheLock)
                {
                    m_assetLocationCache[foundId] = db;
                }
            }
        }

        private void ParseAllowedTypes(IConfig tsConfig)
        {
            if (tsConfig == null)
                return;

            string raw = tsConfig.GetString("TSAssetType", string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string[] tokens = raw.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (sbyte.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte type))
                    m_allowedTypes.Add(type);
                else
                    m_log.WarnFormat("[TSASSET SERVICE]: Ignoring invalid TSAssetType entry '{0}'", tokens[i]);
            }
        }

        private void ParseTypedDatabaseMappings(string rawMappings, string storageProvider)
        {
            if (string.IsNullOrWhiteSpace(rawMappings))
                return;

            string[] lines = rawMappings.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                line = line.Trim('"');
                if (line.EndsWith(";;", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 2);
                else
                    line = line.TrimEnd(';');

                int sep = line.IndexOf(':');
                if (sep <= 0 || sep == line.Length - 1)
                    continue;

                string typeToken = line.Substring(0, sep).Trim();
                string connectionString = line.Substring(sep + 1).Trim();

                if (!sbyte.TryParse(typeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte assetType))
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Ignoring invalid asset type mapping '{0}'", typeToken);
                    continue;
                }

                if (m_allowedTypes.Count > 0 && !m_allowedTypes.Contains(assetType))
                    continue;

                if (string.IsNullOrEmpty(connectionString))
                {
                    m_log.WarnFormat("[TSASSET SERVICE]: Ignoring empty connection string for asset type {0}", assetType);
                    continue;
                }

                IAssetDataPlugin db = GetOrCreateDatabase(storageProvider, connectionString);
                m_typeDatabases[assetType] = db;
                AddProbeDatabase(db);
            }
        }

        private IAssetDataPlugin ResolveDatabaseForType(sbyte type)
        {
            if (m_typeDatabases.TryGetValue(type, out IAssetDataPlugin db))
                return db;

            return m_defaultDatabase;
        }

        private IAssetDataPlugin GetOrCreateDatabase(string storageProvider, string connectionString)
        {
            if (m_connectionDatabases.TryGetValue(connectionString, out IAssetDataPlugin existing))
                return existing;

            IAssetDataPlugin created = CreateAndInitDatabase(storageProvider, connectionString);
            m_connectionDatabases[connectionString] = created;
            return created;
        }

        private IAssetDataPlugin CreateAndInitDatabase(string storageProvider, string connectionString)
        {
            IAssetDataPlugin database = LoadPlugin<IAssetDataPlugin>(storageProvider);
            if (database == null)
                throw new Exception(string.Format("Could not find a storage interface in the module {0}", storageProvider));

            database.Initialise(connectionString);
            return database;
        }

        private void AddProbeDatabase(IAssetDataPlugin db)
        {
            for (int i = 0; i < m_probeOrder.Count; i++)
            {
                if (object.ReferenceEquals(m_probeOrder[i], db))
                    return;
            }

            m_probeOrder.Add(db);
        }

        private void EnqueueFallbackMigration(AssetBase asset)
        {
            if (asset == null)
                return;

            if (m_fallbackMigrationSet.Count >= m_migrationQueueMax)
                return;

            if (m_fallbackMigrationSet.TryAdd(asset.FullID, 0))
                m_fallbackMigrationQueue.Enqueue(asset);
        }

        private void FallbackMigrationWorker()
        {
            while (true)
            {
                Thread.Sleep(m_migrationCheckIntervalMs);

                int requests = Interlocked.Exchange(ref m_requestsInWindow, 0);
                if (requests > m_migrationLowTrafficMaxRequests)
                    continue;

                int migrated = 0;
                while (migrated < m_migrationBatchSize && m_fallbackMigrationQueue.TryDequeue(out AssetBase asset))
                {
                    try
                    {
                        string id = Store(asset);
                        if (!id.Equals(UUID.Zero.ToString(), StringComparison.Ordinal))
                        {
                            if (m_enableFallbackAutoDelete)
                                TryDeleteFromFallback(asset.ID);

                            migrated++;
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("[TSASSET SERVICE]: Auto-migration failed for asset {0}: {1}", asset != null ? asset.FullID.ToString() : "<null>", e.Message);
                    }
                    finally
                    {
                        if (asset != null)
                            m_fallbackMigrationSet.TryRemove(asset.FullID, out _);
                    }
                }

                if (migrated > 0)
                    m_log.InfoFormat("[TSASSET SERVICE]: Auto-migrated {0} fallback assets during low traffic", migrated);
            }
        }

        private void TryDeleteFromFallback(string id)
        {
            if (m_fallbackService == null || string.IsNullOrEmpty(id))
                return;

            try
            {
                if (!m_fallbackService.Delete(id))
                    m_log.WarnFormat("[TSASSET SERVICE]: Fallback auto-delete failed for asset {0}", id);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[TSASSET SERVICE]: Fallback auto-delete exception for asset {0}: {1}", id, e.Message);
            }
        }
    }
}
