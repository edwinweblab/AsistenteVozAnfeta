using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Anfeta.UI.Data;
using Anfeta.UI.Models.Weblab;
using Microsoft.Data.Sqlite;

namespace Anfeta.UI.Services.Speech
{
    public static class LocalIndexPersistence
    {
        private const string INDEX_FILE = "index_cache.json";
        private const string MANIFEST_FILE = "index_manifest.json";
        private const int INDEX_VERSION = 1;

        private static readonly SemaphoreSlim PersistenceGate = new(1, 1);

        private sealed class IndexManifest
        {
            public int Version { get; set; } = INDEX_VERSION;
            public string RootPath { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        }

        public static string GetItemKey(SearchResultRow row)
        {
            if (row == null) return "unknown:" + Guid.NewGuid().ToString("N");

            if (row.Source == SearchSource.Notion)
            {
                if (!string.IsNullOrWhiteSpace(row.ExternalId))
                    return "notion:" + row.ExternalId.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(row.NodeId))
                    return "notion:" + row.NodeId.Trim().ToLowerInvariant();
                return "notion:" + (row.Target ?? string.Empty).Trim().ToLowerInvariant();
            }

            var target = (row.Target ?? string.Empty).Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(target)
                ? "local:" + Guid.NewGuid().ToString("N")
                : "local:" + target;
        }

        public static async Task SaveAsync(
            string rootPath,
            List<SearchResultRow> items,
            CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("Refusing to persist empty index.");

            ct.ThrowIfCancellationRequested();

            var snapshot = new List<SearchResultRow>(items);

            await PersistenceGate.WaitAsync(ct);

            try
            {
                await Task.Run(() =>
                {
                    using var connection = DbConnectionFactory.Create();
                    connection.Open();

                    using var tx = connection.BeginTransaction();

                    // 1. Guardar rootPath en app_settings
                    using (var settingCmd = connection.CreateCommand())
                    {
                        settingCmd.Transaction = tx;
                        settingCmd.CommandText = @"
INSERT INTO app_settings (key, value, updated_at)
VALUES ('search_index_root', @r, CURRENT_TIMESTAMP)
ON CONFLICT(key) DO UPDATE SET value = @r, updated_at = CURRENT_TIMESTAMP;
";
                        settingCmd.Parameters.AddWithValue("@r", rootPath ?? string.Empty);
                        settingCmd.ExecuteNonQuery();
                    }

                    // 2. Reemplazar contenido de search_index
                    using (var deleteCmd = connection.CreateCommand())
                    {
                        deleteCmd.Transaction = tx;
                        deleteCmd.CommandText = "DELETE FROM search_index;";
                        deleteCmd.ExecuteNonQuery();
                    }

                    using (var insertCmd = connection.CreateCommand())
                    {
                        insertCmd.Transaction = tx;
                        insertCmd.CommandText = @"
INSERT OR REPLACE INTO search_index (
    item_key, source, name, target, type, size, server_modified, description,
    search_text, node_id, external_id, external_url, external_source_name,
    project_update_status, scheduled_date, assignment_data_version,
    assignment_keys, notion_edited_utc, is_deleted, root_path, updated_at
) VALUES (
    @key, @source, @name, @target, @type, @size, @smod, @desc,
    @stxt, @nid, @eid, @eurl, @esrc,
    @pstat, @sdate, @adver,
    @akeys, @nedited, @isdel, @rpath, CURRENT_TIMESTAMP
);";

                        var pKey = insertCmd.Parameters.Add("@key", SqliteType.Text);
                        var pSource = insertCmd.Parameters.Add("@source", SqliteType.Integer);
                        var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
                        var pTarget = insertCmd.Parameters.Add("@target", SqliteType.Text);
                        var pType = insertCmd.Parameters.Add("@type", SqliteType.Text);
                        var pSize = insertCmd.Parameters.Add("@size", SqliteType.Integer);
                        var pSMod = insertCmd.Parameters.Add("@smod", SqliteType.Text);
                        var pDesc = insertCmd.Parameters.Add("@desc", SqliteType.Text);
                        var pSTxt = insertCmd.Parameters.Add("@stxt", SqliteType.Text);
                        var pNid = insertCmd.Parameters.Add("@nid", SqliteType.Text);
                        var pEid = insertCmd.Parameters.Add("@eid", SqliteType.Text);
                        var pEurl = insertCmd.Parameters.Add("@eurl", SqliteType.Text);
                        var pEsrc = insertCmd.Parameters.Add("@esrc", SqliteType.Text);
                        var pPstat = insertCmd.Parameters.Add("@pstat", SqliteType.Text);
                        var pSdate = insertCmd.Parameters.Add("@sdate", SqliteType.Text);
                        var pAdver = insertCmd.Parameters.Add("@adver", SqliteType.Integer);
                        var pAkeys = insertCmd.Parameters.Add("@akeys", SqliteType.Text);
                        var pNedited = insertCmd.Parameters.Add("@nedited", SqliteType.Text);
                        var pIsdel = insertCmd.Parameters.Add("@isdel", SqliteType.Integer);
                        var pRpath = insertCmd.Parameters.Add("@rpath", SqliteType.Text);

                        var cleanRoot = rootPath ?? string.Empty;

                        foreach (var item in snapshot)
                        {
                            pKey.Value = GetItemKey(item);
                            pSource.Value = (int)item.Source;
                            pName.Value = item.Name ?? string.Empty;
                            pTarget.Value = item.Target ?? string.Empty;
                            pType.Value = item.Type ?? "FILE";
                            pSize.Value = item.Size;
                            pSMod.Value = (object?)item.ServerModified ?? DBNull.Value;
                            pDesc.Value = (object?)item.Description ?? DBNull.Value;
                            pSTxt.Value = (object?)item.SearchText ?? DBNull.Value;
                            pNid.Value = (object?)item.NodeId ?? DBNull.Value;
                            pEid.Value = (object?)item.ExternalId ?? DBNull.Value;
                            pEurl.Value = (object?)item.ExternalUrl ?? DBNull.Value;
                            pEsrc.Value = (object?)item.ExternalSourceName ?? DBNull.Value;
                            pPstat.Value = (object?)item.ProjectUpdateStatus ?? DBNull.Value;
                            pSdate.Value = (object?)item.ScheduledDate ?? DBNull.Value;
                            pAdver.Value = item.AssignmentDataVersion;
                            pAkeys.Value = item.AssignmentKeys != null && item.AssignmentKeys.Length > 0
                                ? JsonSerializer.Serialize(item.AssignmentKeys)
                                : (object)DBNull.Value;
                            pNedited.Value = item.NotionEditedUtc.HasValue
                                ? item.NotionEditedUtc.Value.ToString("O")
                                : (object)DBNull.Value;
                            pIsdel.Value = item.IsDeleted ? 1 : 0;
                            pRpath.Value = cleanRoot;

                            insertCmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }, ct);
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static async Task UpsertRowAsync(
            SearchResultRow row,
            CancellationToken ct = default)
        {
            if (row == null) return;

            await Task.Run(() =>
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT OR REPLACE INTO search_index (
    item_key, source, name, target, type, size, server_modified, description,
    search_text, node_id, external_id, external_url, external_source_name,
    project_update_status, scheduled_date, assignment_data_version,
    assignment_keys, notion_edited_utc, is_deleted, updated_at
) VALUES (
    @key, @source, @name, @target, @type, @size, @smod, @desc,
    @stxt, @nid, @eid, @eurl, @esrc,
    @pstat, @sdate, @adver,
    @akeys, @nedited, @isdel, CURRENT_TIMESTAMP
);";

                cmd.Parameters.AddWithValue("@key", GetItemKey(row));
                cmd.Parameters.AddWithValue("@source", (int)row.Source);
                cmd.Parameters.AddWithValue("@name", row.Name ?? string.Empty);
                cmd.Parameters.AddWithValue("@target", row.Target ?? string.Empty);
                cmd.Parameters.AddWithValue("@type", row.Type ?? "FILE");
                cmd.Parameters.AddWithValue("@size", row.Size);
                cmd.Parameters.AddWithValue("@smod", (object?)row.ServerModified ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@desc", (object?)row.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@stxt", (object?)row.SearchText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nid", (object?)row.NodeId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@eid", (object?)row.ExternalId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@eurl", (object?)row.ExternalUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@esrc", (object?)row.ExternalSourceName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pstat", (object?)row.ProjectUpdateStatus ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sdate", (object?)row.ScheduledDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@adver", row.AssignmentDataVersion);
                cmd.Parameters.AddWithValue("@akeys", row.AssignmentKeys != null && row.AssignmentKeys.Length > 0
                    ? JsonSerializer.Serialize(row.AssignmentKeys)
                    : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@nedited", row.NotionEditedUtc.HasValue
                    ? row.NotionEditedUtc.Value.ToString("O")
                    : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@isdel", row.IsDeleted ? 1 : 0);

                cmd.ExecuteNonQuery();
            }, ct);
        }

        public static async Task UpsertRowsAsync(
            IEnumerable<SearchResultRow> rows,
            CancellationToken ct = default)
        {
            if (rows == null) return;
            var list = rows.ToList();
            if (list.Count == 0) return;

            await Task.Run(() =>
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();

                using var tx = connection.BeginTransaction();
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR REPLACE INTO search_index (
    item_key, source, name, target, type, size, server_modified, description,
    search_text, node_id, external_id, external_url, external_source_name,
    project_update_status, scheduled_date, assignment_data_version,
    assignment_keys, notion_edited_utc, is_deleted, updated_at
) VALUES (
    @key, @source, @name, @target, @type, @size, @smod, @desc,
    @stxt, @nid, @eid, @eurl, @esrc,
    @pstat, @sdate, @adver,
    @akeys, @nedited, @isdel, CURRENT_TIMESTAMP
);";

                var pKey = cmd.Parameters.Add("@key", SqliteType.Text);
                var pSource = cmd.Parameters.Add("@source", SqliteType.Integer);
                var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                var pTarget = cmd.Parameters.Add("@target", SqliteType.Text);
                var pType = cmd.Parameters.Add("@type", SqliteType.Text);
                var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
                var pSMod = cmd.Parameters.Add("@smod", SqliteType.Text);
                var pDesc = cmd.Parameters.Add("@desc", SqliteType.Text);
                var pSTxt = cmd.Parameters.Add("@stxt", SqliteType.Text);
                var pNid = cmd.Parameters.Add("@nid", SqliteType.Text);
                var pEid = cmd.Parameters.Add("@eid", SqliteType.Text);
                var pEurl = cmd.Parameters.Add("@eurl", SqliteType.Text);
                var pEsrc = cmd.Parameters.Add("@esrc", SqliteType.Text);
                var pPstat = cmd.Parameters.Add("@pstat", SqliteType.Text);
                var pSdate = cmd.Parameters.Add("@sdate", SqliteType.Text);
                var pAdver = cmd.Parameters.Add("@adver", SqliteType.Integer);
                var pAkeys = cmd.Parameters.Add("@akeys", SqliteType.Text);
                var pNedited = cmd.Parameters.Add("@nedited", SqliteType.Text);
                var pIsdel = cmd.Parameters.Add("@isdel", SqliteType.Integer);

                foreach (var item in list)
                {
                    pKey.Value = GetItemKey(item);
                    pSource.Value = (int)item.Source;
                    pName.Value = item.Name ?? string.Empty;
                    pTarget.Value = item.Target ?? string.Empty;
                    pType.Value = item.Type ?? "FILE";
                    pSize.Value = item.Size;
                    pSMod.Value = (object?)item.ServerModified ?? DBNull.Value;
                    pDesc.Value = (object?)item.Description ?? DBNull.Value;
                    pSTxt.Value = (object?)item.SearchText ?? DBNull.Value;
                    pNid.Value = (object?)item.NodeId ?? DBNull.Value;
                    pEid.Value = (object?)item.ExternalId ?? DBNull.Value;
                    pEurl.Value = (object?)item.ExternalUrl ?? DBNull.Value;
                    pEsrc.Value = (object?)item.ExternalSourceName ?? DBNull.Value;
                    pPstat.Value = (object?)item.ProjectUpdateStatus ?? DBNull.Value;
                    pSdate.Value = (object?)item.ScheduledDate ?? DBNull.Value;
                    pAdver.Value = item.AssignmentDataVersion;
                    pAkeys.Value = item.AssignmentKeys != null && item.AssignmentKeys.Length > 0
                        ? JsonSerializer.Serialize(item.AssignmentKeys)
                        : (object)DBNull.Value;
                    pNedited.Value = item.NotionEditedUtc.HasValue
                        ? item.NotionEditedUtc.Value.ToString("O")
                        : (object)DBNull.Value;
                    pIsdel.Value = item.IsDeleted ? 1 : 0;

                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }, ct);
        }

        public static async Task DeleteRowAsync(
            SearchResultRow row,
            CancellationToken ct = default)
        {
            if (row == null) return;
            await DeleteRowByKeyAsync(GetItemKey(row), ct);
        }

        public static async Task DeleteRowByKeyAsync(
            string itemKey,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemKey)) return;

            await Task.Run(() =>
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM search_index WHERE item_key = @key;";
                cmd.Parameters.AddWithValue("@key", itemKey);
                cmd.ExecuteNonQuery();
            }, ct);
        }

        public static async Task<(bool ok, string root, List<SearchResultRow>? items)>
            TryLoadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            await PersistenceGate.WaitAsync(ct);

            try
            {
                // 1) Intentar cargar desde SQLite
                var sqliteResult = await Task.Run(() =>
                {
                    try
                    {
                        using var connection = DbConnectionFactory.Create();
                        connection.Open();

                        string rootPath = string.Empty;
                        using (var settingCmd = connection.CreateCommand())
                        {
                            settingCmd.CommandText = "SELECT value FROM app_settings WHERE key = 'search_index_root' LIMIT 1;";
                            var val = settingCmd.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                                rootPath = val.ToString() ?? string.Empty;
                        }

                        var rows = new List<SearchResultRow>();
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.CommandText = @"
SELECT source, name, target, type, size, server_modified, description,
       search_text, node_id, external_id, external_url, external_source_name,
       project_update_status, scheduled_date, assignment_data_version,
       assignment_keys, notion_edited_utc, is_deleted, root_path
FROM search_index;";

                            using var reader = cmd.ExecuteReader();
                            while (reader.Read())
                            {
                                var row = new SearchResultRow
                                {
                                    Source = (SearchSource)reader.GetInt32(0),
                                    Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                    Target = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                    Type = reader.IsDBNull(3) ? "FILE" : reader.GetString(3),
                                    Size = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                                    ServerModified = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                                    Description = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                                    SearchText = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                                    NodeId = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                                    ExternalId = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                                    ExternalUrl = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                                    ExternalSourceName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                                    ProjectUpdateStatus = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                                    ScheduledDate = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                                    AssignmentDataVersion = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                                    IsDeleted = !reader.IsDBNull(17) && reader.GetInt32(17) == 1
                                };

                                if (!reader.IsDBNull(15))
                                {
                                    try
                                    {
                                        var akeys = reader.GetString(15);
                                        if (!string.IsNullOrWhiteSpace(akeys))
                                            row.AssignmentKeys = JsonSerializer.Deserialize<string[]>(akeys) ?? Array.Empty<string>();
                                    }
                                    catch { }
                                }

                                if (!reader.IsDBNull(16))
                                {
                                    var dtStr = reader.GetString(16);
                                    if (DateTimeOffset.TryParse(dtStr, out var dto))
                                        row.NotionEditedUtc = dto;
                                }

                                if (string.IsNullOrWhiteSpace(rootPath) && !reader.IsDBNull(18))
                                {
                                    rootPath = reader.GetString(18);
                                }

                                rows.Add(row);
                            }
                        }

                        return (hasRows: rows.Count > 0, root: rootPath, items: rows);
                    }
                    catch
                    {
                        return (hasRows: false, root: string.Empty, items: new List<SearchResultRow>());
                    }
                }, ct);

                if (sqliteResult.hasRows)
                {
                    return (true, sqliteResult.root, sqliteResult.items);
                }

                // 2) Fallback para migración inicial: leer index_cache.json si existe
                var folder = ApplicationData.Current.LocalFolder;
                var mf = await folder.TryGetItemAsync(MANIFEST_FILE) as StorageFile;
                var idx = await folder.TryGetItemAsync(INDEX_FILE) as StorageFile;

                if (mf == null || idx == null)
                    return (false, "", null);

                var mfJson = await FileIO.ReadTextAsync(mf);
                var manifest = await Task.Run(
                    () => JsonSerializer.Deserialize<IndexManifest>(mfJson),
                    ct);

                if (manifest == null || manifest.Version != INDEX_VERSION)
                    return (false, "", null);

                var idxJson = await FileIO.ReadTextAsync(idx);
                var items = await Task.Run(
                    () => JsonSerializer.Deserialize<List<SearchResultRow>>(idxJson)
                          ?? new List<SearchResultRow>(),
                    ct);

                if (items.Count > 0)
                {
                    // Guardar de inmediato en SQLite para no tener que parsear JSON de nuevo
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SaveAsync(manifest.RootPath ?? "", items, CancellationToken.None);
                        }
                        catch { }
                    });
                }

                return (true, manifest.RootPath ?? "", items);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (false, "", null);
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static async Task ClearAsync()
        {
            await PersistenceGate.WaitAsync();

            try
            {
                await Task.Run(() =>
                {
                    using var connection = DbConnectionFactory.Create();
                    connection.Open();

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
DELETE FROM search_index;
DELETE FROM app_settings WHERE key = 'search_index_root';
";
                    cmd.ExecuteNonQuery();
                });

                var folder = ApplicationData.Current.LocalFolder;
                var mf = await folder.TryGetItemAsync(MANIFEST_FILE);
                if (mf != null)
                    await mf.DeleteAsync();

                var idx = await folder.TryGetItemAsync(INDEX_FILE);
                if (idx != null)
                    await idx.DeleteAsync();
            }
            finally
            {
                PersistenceGate.Release();
            }
        }

        public static bool RootExists(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return false;

            return Directory.Exists(rootPath);
        }
    }
}
