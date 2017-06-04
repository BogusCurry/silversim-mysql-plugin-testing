﻿// SilverSim is distributed under the terms of the
// GNU Affero General Public License v3 with
// the following clarification and special exception.

// Linking this library statically or dynamically with other modules is
// making a combined work based on this library. Thus, the terms and
// conditions of the GNU Affero General Public License cover the whole
// combination.

// As a special exception, the copyright holders of this library give you
// permission to link this library with independent modules to produce an
// executable, regardless of the license terms of these independent
// modules, and to copy and distribute the resulting executable under
// terms of your choice, provided that you also meet, for each linked
// independent module, the terms and conditions of the license of that
// module. An independent module is a module which is not derived from
// or based on this library. If you modify this library, you may extend
// this exception to your version of the library, but you are not
// obligated to do so. If you do not wish to do so, delete this
// exception statement from your version.

using log4net;
using MySql.Data.MySqlClient;
using Nini.Config;
using SilverSim.Database.MySQL._Migration;
using SilverSim.Main.Common;
using SilverSim.ServiceInterfaces.Asset;
using SilverSim.ServiceInterfaces.Database;
using SilverSim.Threading;
using SilverSim.Types;
using SilverSim.Types.Asset;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;

namespace SilverSim.Database.MySQL.Asset.Deduplication
{
    [Description("MySQL Deduplication Asset Backend")]
    [PluginName("DedupAssets")]
    public sealed class MySQLDedupAssetService : AssetServiceInterface, IDBServiceInterface, IPlugin, IAssetMetadataServiceInterface, IAssetDataServiceInterface
    {
        private static readonly ILog m_Log = LogManager.GetLogger("MYSQL DEDUP ASSET SERVICE");

        private readonly string m_ConnectionString;
        private readonly DefaultAssetReferencesService m_ReferencesService;
        private readonly RwLockedList<string> m_ConfigurationIssues;

        #region Constructor
        public MySQLDedupAssetService(ConfigurationLoader loader, IConfig ownSection)
        {
            m_ConnectionString = MySQLUtilities.BuildConnectionString(ownSection, m_Log);
            m_ConfigurationIssues = loader.KnownConfigurationIssues;
            m_ReferencesService = new DefaultAssetReferencesService(this);
        }

        public void Startup(ConfigurationLoader loader)
        {
            /* intentionally left empty */
        }
        #endregion

        public override bool IsSameServer(AssetServiceInterface other) =>
            other.GetType() == typeof(MySQLDedupAssetService) &&
                (m_ConnectionString == ((MySQLDedupAssetService)other).m_ConnectionString);

        #region Exists methods
        public override bool Exists(UUID key)
        {
            using(var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SELECT id, access_time FROM assetrefs WHERE id = @id", conn))
                {
                    cmd.Parameters.AddParameter("@id", key);
                    using (MySqlDataReader dbReader = cmd.ExecuteReader())
                    {
                        if(dbReader.Read())
                        {
                            if (dbReader.GetDate("access_time") - DateTime.UtcNow > TimeSpan.FromHours(1))
                            {
                                /* update access_time */
                                using(var uconn = new MySqlConnection(m_ConnectionString))
                                {
                                    uconn.Open();
                                    using(var ucmd = new MySqlCommand("UPDATE assets SET access_time = @access WHERE id = @id", uconn))
                                    {
                                        ucmd.Parameters.AddWithValue("@access", Date.GetUnixTime());
                                        ucmd.Parameters.AddWithValue("@id", key);
                                        ucmd.ExecuteNonQuery();
                                    }
                                }
                            }
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public override Dictionary<UUID, bool> Exists(List<UUID> assets)
        {
            var res = new Dictionary<UUID,bool>();
            if (assets.Count == 0)
            {
                return res;
            }

            foreach(UUID id in assets)
            {
                res[id] = false;
            }

            string ids = "'" + string.Join("','", assets) + "'";
            string sql = string.Format("SELECT id, access_time FROM assetrefs WHERE id IN ({0})", ids);

            using (var dbcon = new MySqlConnection(m_ConnectionString))
            {
                dbcon.Open();
                using (var cmd = new MySqlCommand(sql, dbcon))
                {
                    using (MySqlDataReader dbReader = cmd.ExecuteReader())
                    {
                        while (dbReader.Read())
                        {
                            UUID id = dbReader.GetUUID("id");
                            res[id] = true;
                            if (dbReader.GetDate("access_time") - DateTime.UtcNow > TimeSpan.FromHours(1))
                            {
                                /* update access_time */
                                using (MySqlConnection uconn = new MySqlConnection(m_ConnectionString))
                                {
                                    uconn.Open();
                                    using (MySqlCommand ucmd = new MySqlCommand("UPDATE assetrefs SET access_time = @access WHERE id = @id", uconn))
                                    {
                                        ucmd.Parameters.AddWithValue("@access", Date.GetUnixTime());
                                        ucmd.Parameters.AddWithValue("@id", id);
                                        ucmd.ExecuteNonQuery();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return res;
        }
        #endregion

        #region Accessors
        public override AssetData this[UUID key]
        {
            get
            {
                AssetData asset;
                if(!TryGetValue(key, out asset))
                {
                    throw new AssetNotFoundException(key);
                }
                return asset;
            }
        }

        public override bool TryGetValue(UUID key, out AssetData asset)
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SELECT * FROM assetrefs INNER JOIN assetdata ON assetrefs.hash = assetdata.hash AND assetrefs.assetType = assetdata.assetType WHERE id = @id", conn))
                {
                    cmd.Parameters.AddParameter("@id", key);
                    using (MySqlDataReader dbReader = cmd.ExecuteReader())
                    {
                        if (dbReader.Read())
                        {
                            asset = new AssetData()
                            {
                                ID = dbReader.GetUUID("id"),
                                Data = dbReader.GetBytes("data"),
                                Type = dbReader.GetEnum<AssetType>("assetType"),
                                Name = dbReader.GetString("name"),
                                CreateTime = dbReader.GetDate("create_time"),
                                AccessTime = dbReader.GetDate("access_time"),
                                Creator = dbReader.GetUUI("CreatorID"),
                                Flags = dbReader.GetEnum<AssetFlags>("asset_flags"),
                                Temporary = dbReader.GetBool("temporary")
                            };
                            if (asset.AccessTime - DateTime.UtcNow > TimeSpan.FromHours(1))
                            {
                                /* update access_time */
                                using (var uconn = new MySqlConnection(m_ConnectionString))
                                {
                                    uconn.Open();
                                    using (var ucmd = new MySqlCommand("UPDATE assetrefs SET access_time = @access WHERE id = @id", uconn))
                                    {
                                        ucmd.Parameters.AddWithValue("@access", Date.GetUnixTime());
                                        ucmd.Parameters.AddWithValue("@id", key);
                                        ucmd.ExecuteNonQuery();
                                    }
                                }
                            }
                            return true;
                        }
                    }
                }
            }
            asset = null;
            return false;
        }

        #endregion

        #region Metadata interface
        public override IAssetMetadataServiceInterface Metadata => this;

        AssetMetadata IAssetMetadataServiceInterface.this[UUID key]
        {
            get
            {
                AssetMetadata s;
                if (!Metadata.TryGetValue(key, out s))
                {
                    throw new AssetNotFoundException(key);
                }
                return s;
            }
        }

        bool IAssetMetadataServiceInterface.TryGetValue(UUID key, out AssetMetadata metadata)
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SELECT * FROM assetrefs WHERE id=@id", conn))
                {
                    cmd.Parameters.AddParameter("@id", key);
                    using (MySqlDataReader dbReader = cmd.ExecuteReader())
                    {
                        if (dbReader.Read())
                        {
                            metadata = new AssetMetadata()
                            {
                                ID = dbReader.GetUUID("id"),
                                Type = dbReader.GetEnum<AssetType>("assetType"),
                                Name = dbReader.GetString("name"),
                                Creator = dbReader.GetUUI("CreatorID"),
                                CreateTime = dbReader.GetDate("create_time"),
                                AccessTime = dbReader.GetDate("access_time"),
                                Flags = dbReader.GetEnum<AssetFlags>("asset_flags"),
                                Temporary = dbReader.GetBool("temporary")
                            };
                            return true;
                        }
                    }
                }
            }
            metadata = null;
            return false;
        }
        #endregion

        #region References interface
        public override AssetReferencesServiceInterface References => m_ReferencesService;
        #endregion

        #region Data interface
        public override IAssetDataServiceInterface Data => this;

        Stream IAssetDataServiceInterface.this[UUID key]
        {
            get
            {
                Stream s;
                if (!Data.TryGetValue(key, out s))
                {
                    throw new AssetNotFoundException(key);
                }
                return s;
            }
        }

        bool IAssetDataServiceInterface.TryGetValue(UUID key, out Stream s)
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SELECT data FROM assetrefs INNER JOIN assetdata ON assetrefs.hash = assetdata.hash AND assetrefs.assetType = assetdata.assetType WHERE id=@id", conn))
                {
                    cmd.Parameters.AddParameter("@id", key);
                    using (MySqlDataReader dbReader = cmd.ExecuteReader())
                    {
                        if (dbReader.Read())
                        {
                            s = new MemoryStream(dbReader.GetBytes("data"));
                            return true;
                        }
                    }
                }
            }

            s = null;
            return false;
        }
        #endregion

        #region Store asset method
        public override void Store(AssetData asset)
        {
            using (var sha = SHA1.Create())
            {
                byte[] sha1data = sha.ComputeHash(asset.Data);

                using (var conn = new MySqlConnection(m_ConnectionString))
                {
                    conn.Open();

                    conn.InsideTransaction(() =>
                    {
                        using (var cmd =
                            new MySqlCommand(
                                "INSERT INTO assetdata (hash, assetType, data)" +
                                "VALUES(@hash, @assetType, @data) ON DUPLICATE KEY UPDATE assetType=assetType",
                                conn))
                        {
                            using (cmd)
                            {
                                cmd.Parameters.AddParameter("@hash", sha1data);
                                cmd.Parameters.AddParameter("@assetType", asset.Type);
                                cmd.Parameters.AddParameter("@data", asset.Data);
                                if (cmd.ExecuteNonQuery() < 1)
                                {
                                    throw new AssetStoreFailedException(asset.ID);
                                }
                            }
                        }

                        using (var cmd =
                            new MySqlCommand(
                                "INSERT INTO assetrefs (id, name, assetType, temporary, create_time, access_time, asset_flags, CreatorID, hash)" +
                                "VALUES(@id, @name, @assetType, @temporary, @create_time, @access_time, @asset_flags, @CreatorID, @hash)",
                                conn))
                        {
                            string assetName = asset.Name;
                            if (asset.Name.Length > MAX_ASSET_NAME)
                            {
                                assetName = asset.Name.Substring(0, MAX_ASSET_NAME);
                                m_Log.WarnFormat("Name '{0}' for asset {1} truncated from {2} to {3} characters on add",
                                    asset.Name, asset.ID, asset.Name.Length, assetName.Length);
                            }

                            try
                            {
                                using (cmd)
                                {
                                    // create unix epoch time
                                    ulong now = Date.GetUnixTime();
                                    cmd.Parameters.AddParameter("@id", asset.ID);
                                    cmd.Parameters.AddParameter("@name", assetName);
                                    cmd.Parameters.AddParameter("@assetType", asset.Type);
                                    cmd.Parameters.AddParameter("@temporary", asset.Temporary);
                                    cmd.Parameters.AddParameter("@create_time", now);
                                    cmd.Parameters.AddParameter("@access_time", now);
                                    cmd.Parameters.AddParameter("@CreatorID", asset.Creator.ID);
                                    cmd.Parameters.AddParameter("@asset_flags", asset.Flags);
                                    cmd.Parameters.AddParameter("@hash", sha1data);
                                    if (1 > cmd.ExecuteNonQuery())
                                    {
                                        throw new AssetStoreFailedException(asset.ID);
                                    }
                                }
                            }
                            catch
                            {
                                throw new AssetStoreFailedException(asset.ID);
                            }
                        }
                    });
                }
            }
        }
        #endregion

        #region Delete asset method
        public override void Delete(UUID id)
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("DELETE FROM assetrefs WHERE id=@id AND asset_flags <> 0", conn))
                {
                    cmd.Parameters.AddParameter("@id", id);
                }
            }
        }
        #endregion

        #region DBServiceInterface
        public void VerifyConnection()
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                int maxallowedPacket = conn.GetMaxAllowedPacketSize();
                if (maxallowedPacket < 128 * 1024 * 1024)
                {
                    m_ConfigurationIssues.Add(string.Format("Please set max_allowed_packet = 128M in [mysqld] in MySQL/MariaDB. Found {0}", maxallowedPacket));
                }
            }
        }

        public void ProcessMigrations()
        {
            using (var conn = new MySqlConnection(m_ConnectionString))
            {
                conn.Open();
                conn.MigrateTables(Migrations, m_Log);
            }
        }

        private static readonly IMigrationElement[] Migrations = new IMigrationElement[]
        {
            new SqlTable("assetdata") { IsDynamicRowFormat = true },
            new AddColumn<byte[]>("hash") { IsFixed = true, Cardinality = 20, IsNullAllowed = false },
            new AddColumn<AssetType>("assetType") { IsNullAllowed = false },
            new AddColumn<byte[]>("data") { IsLong = true },
            new PrimaryKeyInfo("hash", "assetType"),

            new SqlTable("assetrefs"),
            new AddColumn<UUID>("id") { IsNullAllowed = false, Default = UUID.Zero },
            new AddColumn<string>("name") { Cardinality = 64, IsNullAllowed = false, Default = string.Empty },
            new AddColumn<AssetType>("assetType") { IsNullAllowed = false },
            new AddColumn<bool>("temporary") { IsNullAllowed = false },
            new AddColumn<Date>("create_time") { IsNullAllowed = false },
            new AddColumn<Date>("access_time") { IsNullAllowed = false },
            new AddColumn<AssetFlags>("asset_flags") { IsNullAllowed = false },
            new AddColumn<UUI>("CreatorID") { IsNullAllowed = false, Default = UUID.Zero },
            new AddColumn<byte[]>("hash") { IsFixed = true, IsNullAllowed = false, Cardinality = 20 },
            new PrimaryKeyInfo("id"),
            new TableRevision(2),
            /* normally not executed but needed */
            new ChangeColumn<bool>("temporary") { IsNullAllowed = false },
            new ChangeColumn<AssetFlags>("asset_flags") { IsNullAllowed = false },
            new ChangeColumn<UUI>("CreatorID") { IsNullAllowed = false, Default = UUID.Zero },
        };
        #endregion

        private const int MAX_ASSET_NAME = 64;
    }
}