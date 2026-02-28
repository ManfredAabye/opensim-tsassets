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
using System.Data;
using System.Globalization;
using System.Reflection;
using log4net;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data;

namespace OpenSim.Data.MySQL
{
	/// <summary>
	/// MySQL storage provider that stores assets in type-specific tables:
	/// assets_{assetType}. Negative asset types produce table names with a
	/// minus sign, e.g. assets_-2. A legacy fallback to table "assets" can
	/// be enabled explicitly.
	/// </summary>
	public class MySQLtsAssetData : AssetDataBase
	{
		private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		private const string LegacyTableName = "assets";
		private const string IndexTableName = "tsassets_index";
		private const string TypedTableNameFormat = "assets_{0}";
		private const bool DefaultFallbackToLegacy = false;

		private readonly object m_tableSync = new object();
		private readonly HashSet<string> m_initializedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private string m_connectionString;
		private bool m_fallbackToLegacy;

		protected virtual Assembly Assembly
		{
			get { return GetType().Assembly; }
		}

		#region IPlugin Members

		public override string Version
		{
			get { return "1.0.0.0"; }
		}

		public override string Name
		{
			get { return "MySQL TSAsset storage engine"; }
		}

		public override void Initialise(string connect)
		{
			if (string.IsNullOrEmpty(connect))
				throw new ArgumentException("Connection string must not be null or empty", nameof(connect));

			m_connectionString = connect;
			m_fallbackToLegacy = TryReadBooleanSetting(connect, "TSFallbackToLegacyAssets", DefaultFallbackToLegacy);

			using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
			{
				dbcon.Open();

				Migration migration = new Migration(dbcon, Assembly, "TSAssetStore");
				migration.Update();

				EnsureIndexTable(dbcon);
			}
		}

		public override void Initialise()
		{
			throw new NotImplementedException();
		}

		public override void Dispose()
		{
		}

		#endregion

		#region IAssetDataPlugin Members

		public override AssetBase GetAsset(UUID assetID)
		{
			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					if (TryGetIndexedAssetType(dbcon, assetID, out sbyte indexedType))
					{
						string typedTable = GetTypeTableName(indexedType);

						AssetBase typedAsset = GetAssetFromTable(dbcon, typedTable, assetID);
						if (typedAsset != null)
							return typedAsset;

						RemoveIndexRow(dbcon, assetID);
					}

					if (m_fallbackToLegacy)
					{
						AssetBase legacyAsset = GetAssetFromTable(dbcon, LegacyTableName, assetID);
						if (legacyAsset != null)
						{
							TryStoreTypedWithExistingConnection(dbcon, legacyAsset);
							return legacyAsset;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure fetching asset {0}. Error: {1}", assetID, e.Message);
			}

			return null;
		}

		public override bool StoreAsset(AssetBase asset)
		{
			if (asset == null)
				return false;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					return TryStoreTypedWithExistingConnection(dbcon, asset);
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure storing asset {0}. Error: {1}", asset.FullID, e.Message);
				return false;
			}
		}

		public override bool[] AssetsExist(UUID[] uuids)
		{
			if (uuids == null || uuids.Length == 0)
				return new bool[0];

			bool[] results = new bool[uuids.Length];
			Dictionary<UUID, List<int>> positions = new Dictionary<UUID, List<int>>();

			for (int i = 0; i < uuids.Length; i++)
			{
				if (!positions.TryGetValue(uuids[i], out List<int> indexes))
				{
					indexes = new List<int>();
					positions[uuids[i]] = indexes;
				}

				indexes.Add(i);
			}

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					HashSet<UUID> found = QueryExistingIds(dbcon, IndexTableName, "id", uuids);

					if (m_fallbackToLegacy)
					{
						UUID[] missing = BuildMissingArray(uuids, found);
						if (missing.Length > 0)
						{
							HashSet<UUID> legacyFound = QueryExistingIds(dbcon, LegacyTableName, "id", missing);
							foreach (UUID id in legacyFound)
								found.Add(id);
						}
					}

					foreach (UUID id in found)
					{
						if (positions.TryGetValue(id, out List<int> idxList))
						{
							foreach (int idx in idxList)
								results[idx] = true;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure checking asset existence. Error: {0}", e.Message);
			}

			return results;
		}

		public override List<AssetMetadata> FetchAssetMetadataSet(int start, int count)
		{
			List<AssetMetadata> result = new List<AssetMetadata>(Math.Max(0, count));
			List<KeyValuePair<UUID, sbyte>> pending = new List<KeyValuePair<UUID, sbyte>>(Math.Max(0, count));

			if (count <= 0)
				return result;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					using (MySqlCommand cmd = new MySqlCommand(
						$"SELECT id, assetType FROM `{IndexTableName}` ORDER BY updated_at DESC LIMIT ?start, ?count",
						dbcon))
					{
						cmd.Parameters.AddWithValue("?start", start);
						cmd.Parameters.AddWithValue("?count", count);

						using (MySqlDataReader dbReader = cmd.ExecuteReader())
						{
							while (dbReader.Read())
							{
								UUID id = DBGuid.FromDB(dbReader["id"]);
								int typeInt = Convert.ToInt32(dbReader["assetType"], CultureInfo.InvariantCulture);
								sbyte type = Convert.ToSByte(typeInt, CultureInfo.InvariantCulture);
								pending.Add(new KeyValuePair<UUID, sbyte>(id, type));
							}
						}

						for (int i = 0; i < pending.Count; i++)
						{
							AssetMetadata metadata = GetMetadataByIdAndType(dbcon, pending[i].Key, pending[i].Value);
							if (metadata != null)
								result.Add(metadata);
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure fetching metadata set from {0}, count {1}. Error: {2}", start, count, e.Message);
			}

			return result;
		}

		public override bool Delete(string id)
		{
			if (!UUID.TryParse(id, out UUID assetId))
				return false;

			try
			{
				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();

					using (MySqlTransaction tx = dbcon.BeginTransaction())
					{
						try
						{
							if (TryGetIndexedAssetType(dbcon, tx, assetId, out sbyte indexedType))
							{
								string typedTable = GetTypeTableName(indexedType);

								using (MySqlCommand deleteTyped = new MySqlCommand($"DELETE FROM `{typedTable}` WHERE id=?id", dbcon, tx))
								{
									deleteTyped.Parameters.AddWithValue("?id", assetId.ToString());
									deleteTyped.ExecuteNonQuery();
								}
							}

							using (MySqlCommand deleteIndex = new MySqlCommand($"DELETE FROM `{IndexTableName}` WHERE id=?id", dbcon, tx))
							{
								deleteIndex.Parameters.AddWithValue("?id", assetId.ToString());
								deleteIndex.ExecuteNonQuery();
							}

							if (m_fallbackToLegacy)
							{
								using (MySqlCommand deleteLegacy = new MySqlCommand($"DELETE FROM `{LegacyTableName}` WHERE id=?id", dbcon, tx))
								{
									deleteLegacy.Parameters.AddWithValue("?id", assetId.ToString());
									deleteLegacy.ExecuteNonQuery();
								}
							}

							tx.Commit();
							return true;
						}
						catch
						{
							tx.Rollback();
							throw;
						}
					}
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[TSASSET DB]: MySQL failure deleting asset {0}. Error: {1}", id, e.Message);
				return false;
			}
		}

		#endregion

		private static bool TryReadBooleanSetting(string connectionString, string key, bool defaultValue)
		{
			if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(key))
				return defaultValue;

			string[] parts = connectionString.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string rawPart in parts)
			{
				string part = rawPart.Trim();
				int eqIndex = part.IndexOf('=');
				if (eqIndex <= 0 || eqIndex == part.Length - 1)
					continue;

				string k = part.Substring(0, eqIndex).Trim();
				if (!k.Equals(key, StringComparison.OrdinalIgnoreCase))
					continue;

				string v = part.Substring(eqIndex + 1).Trim();
				if (v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
					return true;

				if (v.Equals("0", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
					return false;
			}

			return defaultValue;
		}

		private static string GetTypeTableName(sbyte assetType)
		{
			return string.Format(CultureInfo.InvariantCulture, TypedTableNameFormat, assetType);
		}

		private void EnsureTypeTable(string tableName)
		{
			lock (m_tableSync)
			{
				if (m_initializedTables.Contains(tableName))
					return;

				using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
				{
					dbcon.Open();
					CreateTypeTable(dbcon, tableName);
				}

				m_initializedTables.Add(tableName);
			}
		}

		private void CreateTypeTable(MySqlConnection dbcon, string tableName)
		{
			string sql =
				$"CREATE TABLE IF NOT EXISTS `{tableName}` (" +
				"`name` varchar(64) NOT NULL," +
				"`description` varchar(64) NOT NULL," +
				"`assetType` tinyint(4) NOT NULL," +
				"`local` tinyint(1) NOT NULL," +
				"`temporary` tinyint(1) NOT NULL," +
				"`data` longblob NOT NULL," +
				"`id` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'," +
				"`create_time` int(11) DEFAULT '0'," +
				"`access_time` int(11) DEFAULT '0'," +
				"`asset_flags` int(11) NOT NULL DEFAULT '0'," +
				"`CreatorID` varchar(128) NOT NULL DEFAULT ''," +
				"PRIMARY KEY (`id`)" +
				") ENGINE=InnoDB DEFAULT CHARSET=utf8";

			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
			{
				cmd.ExecuteNonQuery();
			}
		}

		private static void EnsureIndexTable(MySqlConnection dbcon)
		{
			string sql =
				$"CREATE TABLE IF NOT EXISTS `{IndexTableName}` (" +
				"`id` char(36) NOT NULL," +
				"`assetType` tinyint(4) NOT NULL," +
				"`updated_at` int(11) NOT NULL DEFAULT '0'," +
				"PRIMARY KEY (`id`)," +
				$"INDEX `idx_{IndexTableName}_assetType` (`assetType`)" +
				") ENGINE=InnoDB DEFAULT CHARSET=utf8";

			using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
			{
				cmd.ExecuteNonQuery();
			}
		}

		private bool TryStoreTypedWithExistingConnection(MySqlConnection dbcon, AssetBase asset)
		{
			string tableName = GetTypeTableName(asset.Type);
			EnsureTypeTable(tableName);

			string assetName = asset.Name ?? string.Empty;
			if (assetName.Length > AssetBase.MAX_ASSET_NAME)
				assetName = assetName.Substring(0, AssetBase.MAX_ASSET_NAME);

			string assetDescription = asset.Description ?? string.Empty;
			if (assetDescription.Length > AssetBase.MAX_ASSET_DESC)
				assetDescription = assetDescription.Substring(0, AssetBase.MAX_ASSET_DESC);

			int now = (int)Utils.DateTimeToUnixTime(DateTime.UtcNow);

			using (MySqlTransaction tx = dbcon.BeginTransaction())
			{
				try
				{
					string upsertAssetSql =
						$"REPLACE INTO `{tableName}` " +
						"(id, name, description, assetType, local, temporary, create_time, access_time, asset_flags, CreatorID, data) " +
						"VALUES(?id, ?name, ?description, ?assetType, ?local, ?temporary, ?create_time, ?access_time, ?asset_flags, ?CreatorID, ?data)";

					using (MySqlCommand assetCmd = new MySqlCommand(upsertAssetSql, dbcon, tx))
					{
						assetCmd.Parameters.AddWithValue("?id", asset.ID);
						assetCmd.Parameters.AddWithValue("?name", assetName);
						assetCmd.Parameters.AddWithValue("?description", assetDescription);
						assetCmd.Parameters.AddWithValue("?assetType", asset.Type);
						assetCmd.Parameters.AddWithValue("?local", asset.Local);
						assetCmd.Parameters.AddWithValue("?temporary", asset.Temporary);
						assetCmd.Parameters.AddWithValue("?create_time", now);
						assetCmd.Parameters.AddWithValue("?access_time", now);
						assetCmd.Parameters.AddWithValue("?asset_flags", (int)asset.Flags);
						assetCmd.Parameters.AddWithValue("?CreatorID", asset.Metadata.CreatorID ?? string.Empty);
						assetCmd.Parameters.AddWithValue("?data", asset.Data ?? Array.Empty<byte>());
						assetCmd.ExecuteNonQuery();
					}

					using (MySqlCommand indexCmd = new MySqlCommand(
						$"REPLACE INTO `{IndexTableName}` (id, assetType, updated_at) VALUES (?id, ?assetType, ?updated_at)",
						dbcon,
						tx))
					{
						indexCmd.Parameters.AddWithValue("?id", asset.ID);
						indexCmd.Parameters.AddWithValue("?assetType", asset.Type);
						indexCmd.Parameters.AddWithValue("?updated_at", now);
						indexCmd.ExecuteNonQuery();
					}

					tx.Commit();
					return true;
				}
				catch
				{
					tx.Rollback();
					throw;
				}
			}
		}

		private AssetBase GetAssetFromTable(MySqlConnection dbcon, string tableName, UUID assetID)
		{
			string sql =
				$"SELECT name, description, assetType, local, temporary, asset_flags, CreatorID, data " +
				$"FROM `{tableName}` WHERE id=?id";

			try
			{
				using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
				{
					cmd.Parameters.AddWithValue("?id", assetID.ToString());

					using (MySqlDataReader dbReader = cmd.ExecuteReader(CommandBehavior.SingleRow))
					{
						if (!dbReader.Read())
							return null;

						AssetBase asset = new AssetBase(assetID, (string)dbReader["name"], (sbyte)dbReader["assetType"], dbReader["CreatorID"].ToString());
						asset.Description = (string)dbReader["description"];
						asset.Local = Convert.ToBoolean(dbReader["local"]);
						asset.Temporary = Convert.ToBoolean(dbReader["temporary"]);
						asset.Flags = (AssetFlags)Convert.ToInt32(dbReader["asset_flags"], CultureInfo.InvariantCulture);
						asset.Data = (byte[])dbReader["data"];
						return asset;
					}
				}
			}
			catch (MySqlException)
			{
				return null;
			}
		}

		private AssetMetadata GetMetadataByIdAndType(MySqlConnection dbcon, UUID id, sbyte type)
		{
			string tableName = GetTypeTableName(type);

			string sql =
				$"SELECT id, name, description, assetType, temporary, asset_flags, CreatorID " +
				$"FROM `{tableName}` WHERE id=?id";

			try
			{
				using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
				{
					cmd.Parameters.AddWithValue("?id", id.ToString());
					using (MySqlDataReader dbReader = cmd.ExecuteReader(CommandBehavior.SingleRow))
					{
						if (dbReader.Read())
							return ReadMetadata(dbReader);
					}
				}
			}
			catch (MySqlException)
			{
				// Type table may not yet exist if no asset of this type was stored.
			}

			if (!m_fallbackToLegacy)
				return null;

			using (MySqlCommand legacyCmd = new MySqlCommand(
				$"SELECT id, name, description, assetType, temporary, asset_flags, CreatorID FROM `{LegacyTableName}` WHERE id=?id",
				dbcon))
			{
				legacyCmd.Parameters.AddWithValue("?id", id.ToString());
				using (MySqlDataReader dbReader = legacyCmd.ExecuteReader(CommandBehavior.SingleRow))
				{
					if (dbReader.Read())
						return ReadMetadata(dbReader);
				}
			}

			return null;
		}

		private static AssetMetadata ReadMetadata(MySqlDataReader dbReader)
		{
			AssetMetadata metadata = new AssetMetadata();
			metadata.FullID = DBGuid.FromDB(dbReader["id"]);
			metadata.Name = dbReader["name"].ToString();
			metadata.Description = dbReader["description"].ToString();
			metadata.Type = Convert.ToSByte(dbReader["assetType"], CultureInfo.InvariantCulture);
			metadata.Temporary = Convert.ToBoolean(dbReader["temporary"]);
			metadata.Flags = (AssetFlags)Convert.ToInt32(dbReader["asset_flags"], CultureInfo.InvariantCulture);
			metadata.CreatorID = dbReader["CreatorID"].ToString();
			metadata.SHA1 = Array.Empty<byte>();
			return metadata;
		}

		private static UUID[] BuildMissingArray(UUID[] requested, HashSet<UUID> found)
		{
			List<UUID> missing = new List<UUID>(requested.Length);
			for (int i = 0; i < requested.Length; i++)
			{
				if (!found.Contains(requested[i]))
					missing.Add(requested[i]);
			}

			return missing.ToArray();
		}

		private static HashSet<UUID> QueryExistingIds(MySqlConnection dbcon, string tableName, string idColumn, UUID[] uuids)
		{
			HashSet<UUID> found = new HashSet<UUID>();

			if (uuids.Length == 0)
				return found;

			const int batchSize = 200;
			for (int offset = 0; offset < uuids.Length; offset += batchSize)
			{
				int take = Math.Min(batchSize, uuids.Length - offset);
				string[] placeholders = new string[take];

				using (MySqlCommand cmd = dbcon.CreateCommand())
				{
					for (int i = 0; i < take; i++)
					{
						string paramName = "?id" + i.ToString(CultureInfo.InvariantCulture);
						placeholders[i] = paramName;
						cmd.Parameters.AddWithValue(paramName, uuids[offset + i].ToString());
					}

					cmd.CommandText = string.Format(
						CultureInfo.InvariantCulture,
						"SELECT {0} FROM `{1}` WHERE {0} IN ({2})",
						idColumn,
						tableName,
						string.Join(",", placeholders));

					using (MySqlDataReader dbReader = cmd.ExecuteReader())
					{
						while (dbReader.Read())
						{
							UUID id = DBGuid.FromDB(dbReader[idColumn]);
							found.Add(id);
						}
					}
				}
			}

			return found;
		}

		private bool TryGetIndexedAssetType(MySqlConnection dbcon, UUID assetID, out sbyte assetType)
		{
			return TryGetIndexedAssetType(dbcon, null, assetID, out assetType);
		}

		private bool TryGetIndexedAssetType(MySqlConnection dbcon, MySqlTransaction tx, UUID assetID, out sbyte assetType)
		{
			using (MySqlCommand cmd = new MySqlCommand($"SELECT assetType FROM `{IndexTableName}` WHERE id=?id", dbcon, tx))
			{
				cmd.Parameters.AddWithValue("?id", assetID.ToString());

				object value = cmd.ExecuteScalar();
				if (value == null || value is DBNull)
				{
					assetType = 0;
					return false;
				}

				int typeInt = Convert.ToInt32(value, CultureInfo.InvariantCulture);
				if (typeInt < sbyte.MinValue || typeInt > sbyte.MaxValue)
				{
					assetType = 0;
					return false;
				}

				assetType = (sbyte)typeInt;
				return true;
			}
		}

		private static void RemoveIndexRow(MySqlConnection dbcon, UUID assetID)
		{
			using (MySqlCommand cmd = new MySqlCommand($"DELETE FROM `{IndexTableName}` WHERE id=?id", dbcon))
			{
				cmd.Parameters.AddWithValue("?id", assetID.ToString());
				cmd.ExecuteNonQuery();
			}
		}
	}
}
