// Data/LocalAppsRepository.cs
using Anfeta.UI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Data
{
    public sealed class LocalAppsRepository
    {
        public List<LocalAppEntry> GetAll()
        {
            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT app_key, friendly_name, category, executable_name, executable_path, enabled, source
FROM local_apps
ORDER BY friendly_name COLLATE NOCASE;
";

            var list = new List<LocalAppEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new LocalAppEntry
                {
                    AppKey = r.GetString(0),
                    FriendlyName = r.GetString(1),
                    Category = r.IsDBNull(2) ? "otro" : r.GetString(2),
                    ExecutableName = r.IsDBNull(3) ? "" : r.GetString(3),
                    ExecutablePath = r.IsDBNull(4) ? null : r.GetString(4),
                    Enabled = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                    Source = r.IsDBNull(6) ? null : r.GetString(6)
                });
            }
            return list;
        }

        public List<LocalAppEntry> GetEnabled()
        {
            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT app_key, friendly_name, category, executable_name, executable_path, enabled, source
FROM local_apps
WHERE enabled = 1
ORDER BY friendly_name COLLATE NOCASE;
";

            var list = new List<LocalAppEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new LocalAppEntry
                {
                    AppKey = r.GetString(0),
                    FriendlyName = r.GetString(1),
                    Category = r.IsDBNull(2) ? "otro" : r.GetString(2),
                    ExecutableName = r.IsDBNull(3) ? "" : r.GetString(3),
                    ExecutablePath = r.IsDBNull(4) ? null : r.GetString(4),
                    Enabled = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                    Source = r.IsDBNull(6) ? null : r.GetString(6)
                });
            }
            return list;
        }

        public LocalAppEntry? GetByKey(string appKey)
        {
            var key = NormalizeKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) return null;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT app_key, friendly_name, category, executable_name, executable_path, enabled, source
FROM local_apps
WHERE app_key = @k
LIMIT 1;
";
            cmd.Parameters.AddWithValue("@k", key);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return new LocalAppEntry
            {
                AppKey = r.GetString(0),
                FriendlyName = r.GetString(1),
                Category = r.IsDBNull(2) ? "otro" : r.GetString(2),
                ExecutableName = r.IsDBNull(3) ? "" : r.GetString(3),
                ExecutablePath = r.IsDBNull(4) ? null : r.GetString(4),
                Enabled = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                Source = r.IsDBNull(6) ? null : r.GetString(6)
            };
        }

        public bool ExistsAppKey(string appKey)
        {
            var key = NormalizeKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) return false;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT 1 FROM local_apps WHERE app_key = @k LIMIT 1;";
            cmd.Parameters.AddWithValue("@k", key);

            var obj = cmd.ExecuteScalar();
            return obj != null;
        }

        public List<string> GetSynonyms(string appKey)
        {
            var key = NormalizeKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) return new List<string>();

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT synonym
FROM local_app_synonyms
WHERE app_key = @k
ORDER BY synonym COLLATE NOCASE;
";
            cmd.Parameters.AddWithValue("@k", key);

            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(r.GetString(0));

            return list;
        }

        public bool ExistsSynonym(string appKey, string synonym)
        {
            var key = NormalizeKey(appKey);
            var syn = NormalizeSyn(synonym);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(syn)) return false;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT 1
FROM local_app_synonyms
WHERE app_key = @k AND synonym = @s
LIMIT 1;
";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@s", syn);

            return cmd.ExecuteScalar() != null;
        }

        public void AddSynonym(string appKey, string synonym)
        {
            var key = NormalizeKey(appKey);
            var syn = NormalizeSyn(synonym);
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("appKey requerido");
            if (string.IsNullOrWhiteSpace(syn)) return;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT OR IGNORE INTO local_app_synonyms(app_key, synonym, created_at, updated_at)
VALUES (@k, @s, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@s", syn);
            cmd.ExecuteNonQuery();
        }

        public void DeleteSynonym(string appKey, string synonym)
        {
            var key = NormalizeKey(appKey);
            var syn = NormalizeSyn(synonym);
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("appKey requerido");
            if (string.IsNullOrWhiteSpace(syn)) return;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DELETE FROM local_app_synonyms
WHERE app_key = @k AND synonym = @s;
";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@s", syn);
            cmd.ExecuteNonQuery();
        }

        public void ReplaceSynonyms(string appKey, IEnumerable<string> synonyms)
        {
            var key = NormalizeKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("appKey requerido");

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var tx = con.BeginTransaction();

            using (var del = con.CreateCommand())
            {
                del.CommandText = @"DELETE FROM local_app_synonyms WHERE app_key = @k;";
                del.Parameters.AddWithValue("@k", key);
                del.ExecuteNonQuery();
            }

            foreach (var s in synonyms ?? Array.Empty<string>())
            {
                var syn = NormalizeSyn(s);
                if (string.IsNullOrWhiteSpace(syn)) continue;

                using var ins = con.CreateCommand();
                ins.CommandText = @"
INSERT OR IGNORE INTO local_app_synonyms(app_key, synonym, created_at, updated_at)
VALUES (@k, @s, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
";
                ins.Parameters.AddWithValue("@k", key);
                ins.Parameters.AddWithValue("@s", syn);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public void SetEnabled(string appKey, bool enabled)
        {
            var key = NormalizeKey(appKey);
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("appKey requerido");

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
UPDATE local_apps
SET enabled = @en,
    updated_at = CURRENT_TIMESTAMP
WHERE app_key = @k;
";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@en", enabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Inserta o actualiza app (manual/detected/seed).
        /// Nota: app.AppKey se normaliza a minúsculas.
        /// </summary>
        public void UpsertApp(LocalAppEntry app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (string.IsNullOrWhiteSpace(app.AppKey)) throw new ArgumentException("AppKey requerido");
            if (string.IsNullOrWhiteSpace(app.FriendlyName)) throw new ArgumentException("FriendlyName requerido");
            if (string.IsNullOrWhiteSpace(app.Category)) app.Category = "otro";
            if (string.IsNullOrWhiteSpace(app.ExecutableName)) throw new ArgumentException("ExecutableName requerido");

            var key = NormalizeKey(app.AppKey);
            var source = string.IsNullOrWhiteSpace(app.Source) ? "manual" : app.Source.Trim().ToLowerInvariant();

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO local_apps(app_key, friendly_name, category, executable_name, executable_path, enabled, source, created_at, updated_at)
VALUES(@k, @n, @c, @exe, @path, @en, @src, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT(app_key) DO UPDATE SET
    friendly_name = excluded.friendly_name,
    category = excluded.category,
    executable_name = excluded.executable_name,
    executable_path = excluded.executable_path,
    enabled = excluded.enabled,
    source = excluded.source,
    updated_at = CURRENT_TIMESTAMP;
";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@n", app.FriendlyName);
            cmd.Parameters.AddWithValue("@c", app.Category);
            cmd.Parameters.AddWithValue("@exe", app.ExecutableName);
            cmd.Parameters.AddWithValue("@path", (object?)app.ExecutablePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@en", app.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@src", source);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Upsert seguro para apps detectadas:
        /// - No pisa "enabled" si el usuario ya la habilitó/deshabilitó.
        /// - Marca source="detected".
        /// - Opcional: no sobreescribe apps "seed" (whitelist inicial).
        /// </summary>
        public void UpsertDetectedAppSafe(LocalAppEntry detected)
        {
            if (detected == null) throw new ArgumentNullException(nameof(detected));
            if (string.IsNullOrWhiteSpace(detected.AppKey)) return;
            if (string.IsNullOrWhiteSpace(detected.FriendlyName)) return;
            if (string.IsNullOrWhiteSpace(detected.ExecutableName)) return;

            var key = NormalizeKey(detected.AppKey);

            var existing = GetByKey(key);

            // Si ya existe, preserva enabled del usuario
            detected.AppKey = key;
            detected.Source = "detected";
            detected.Enabled = existing?.Enabled ?? false;

            // Si ya es seed, no lo pises (evita que detected reemplace tu whitelist)
            if (existing != null && string.Equals(existing.Source, "seed", StringComparison.OrdinalIgnoreCase))
                return;

            UpsertApp(detected);
        }

        /// <summary>
        /// Resolver por texto (sinónimo o nombre). Útil para IntentValidator/normalización.
        /// Prioriza apps habilitadas.
        /// </summary>
        public LocalAppEntry? ResolveByText(string text)
        {
            var q = NormalizeSyn(text);
            if (string.IsNullOrWhiteSpace(q)) return null;

            using var con = DbConnectionFactory.Create();
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT a.app_key, a.friendly_name, a.category, a.executable_name, a.executable_path, a.enabled, a.source
FROM local_apps a
LEFT JOIN local_app_synonyms s
    ON s.app_key = a.app_key
WHERE
    a.app_key = @q
    OR LOWER(a.friendly_name) = @q
    OR s.synonym = @q
ORDER BY a.enabled DESC, a.friendly_name COLLATE NOCASE
LIMIT 1;
";
            cmd.Parameters.AddWithValue("@q", q);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return new LocalAppEntry
            {
                AppKey = r.GetString(0),
                FriendlyName = r.GetString(1),
                Category = r.IsDBNull(2) ? "otro" : r.GetString(2),
                ExecutableName = r.IsDBNull(3) ? "" : r.GetString(3),
                ExecutablePath = r.IsDBNull(4) ? null : r.GetString(4),
                Enabled = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                Source = r.IsDBNull(6) ? null : r.GetString(6)
            };
        }

        private static string NormalizeKey(string? appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return "";
            return appKey.Trim().ToLowerInvariant();
        }

        private static string NormalizeSyn(string? synonymOrText)
        {
            if (string.IsNullOrWhiteSpace(synonymOrText)) return "";
            return synonymOrText.Trim().ToLowerInvariant();
        }
    }
}