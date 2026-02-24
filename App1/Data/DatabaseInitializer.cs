using Microsoft.Data.Sqlite;
using System.IO;
using Windows.Storage;

namespace Anfeta.UI.Data
{
    public static class DatabaseInitializer
    {
        public static string GetDatabasePath()
        {
            // Para apps empaquetadas (MSIX)
            string folder = ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(folder, "asistente.db");
        }

        private static void SeedLocalApps(SqliteConnection connection)
        {
            // Insert idempotente: solo inserta si NO existe app_key
            using var tx = connection.BeginTransaction();

            void UpsertApp(string key, string name, string category, string exeName, int enabled, string source)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT INTO local_apps(app_key, friendly_name, category, executable_name, enabled, source, created_at, updated_at)
SELECT @k, @n, @c, @exe, @en, @src, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM local_apps WHERE app_key = @k);
";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@c", category);
                cmd.Parameters.AddWithValue("@exe", exeName);
                cmd.Parameters.AddWithValue("@en", enabled);
                cmd.Parameters.AddWithValue("@src", source);
                cmd.ExecuteNonQuery();
            }

            void AddSyn(string key, string synonym)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT OR IGNORE INTO local_app_synonyms(app_key, synonym, created_at, updated_at)
VALUES (@k, @s, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@s", synonym.Trim().ToLowerInvariant());
                cmd.ExecuteNonQuery();
            }

            // Base (habilitadas)
            UpsertApp("chrome", "Chrome", "navegador", "chrome.exe", 1, "seed");
            AddSyn("chrome", "chrome");
            AddSyn("chrome", "navegador");
            AddSyn("chrome", "browser");
            AddSyn("chrome", "internet");
            AddSyn("chrome", "web");

            UpsertApp("calculadora", "Calculadora", "utilidad", "calc.exe", 1, "seed");
            AddSyn("calculadora", "calculadora");
            AddSyn("calculadora", "calc");
            AddSyn("calculadora", "calcular");

            UpsertApp("bloc", "Bloc de Notas", "editor", "notepad.exe", 1, "seed");
            AddSyn("bloc", "bloc");
            AddSyn("bloc", "bloc de notas");
            AddSyn("bloc", "notepad");
            AddSyn("bloc", "notas");
            AddSyn("bloc", "editor");

            UpsertApp("explorador", "Explorador de archivos", "sistema", "explorer.exe", 1, "seed");
            AddSyn("explorador", "explorador");
            AddSyn("explorador", "archivos");
            AddSyn("explorador", "carpetas");
            AddSyn("explorador", "explorer");

            // Plantillas (deshabilitadas por default)
            UpsertApp("word", "Microsoft Word", "office", "winword.exe", 0, "seed");
            AddSyn("word", "word");
            AddSyn("word", "microsoft word");
            AddSyn("word", "winword");

            UpsertApp("excel", "Microsoft Excel", "office", "excel.exe", 0, "seed");
            AddSyn("excel", "excel");
            AddSyn("excel", "microsoft excel");

            UpsertApp("powerpoint", "Microsoft PowerPoint", "office", "powerpnt.exe", 0, "seed");
            AddSyn("powerpoint", "powerpoint");
            AddSyn("powerpoint", "microsoft powerpoint");
            AddSyn("powerpoint", "ppt");

            tx.Commit();
        }

        public static void InitializeDatabase()
        {
            string dbPath = GetDatabasePath();

            // Log para verificar ruta
            System.Diagnostics.Debug.WriteLine("RUTA BD: " + dbPath);

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS api_keys (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    provider VARCHAR NOT NULL,
    name VARCHAR,
    api_key TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 0,
    last_validated_at DATETIME,
    created_at DATETIME,
    updated_at DATETIME
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_api_keys_single_active_provider
ON api_keys(provider)
WHERE is_active = 1;

CREATE INDEX IF NOT EXISTS idx_api_keys_provider
ON api_keys(provider);

-- ========================
-- CONTROL DE ESQUEMA
-- ========================
CREATE TABLE IF NOT EXISTS schema_info (
    id INTEGER PRIMARY KEY,
    version INTEGER,
    updated_at DATETIME
);

-- ========================
-- CONFIGURACIÓN DE LA APP
-- ========================
CREATE TABLE IF NOT EXISTS app_settings (
    key VARCHAR PRIMARY KEY,
    value TEXT,
    updated_at DATETIME
);

-- ========================
-- CACHE DE API
-- ========================
CREATE TABLE IF NOT EXISTS api_cache (
    cache_key VARCHAR PRIMARY KEY,
    payload_json TEXT,
    fetched_at DATETIME
);

-- ========================
-- ESTADO DEL SISTEMA
-- ========================
CREATE TABLE IF NOT EXISTS runtime_state (
    id INTEGER PRIMARY KEY,
    has_internet BOOLEAN,
    is_api_reachable BOOLEAN,
    last_online_at DATETIME,
    last_api_ok_at DATETIME,
    updated_at DATETIME
);

-- ========================
-- RECORDATORIOS
-- ========================
CREATE TABLE IF NOT EXISTS reminders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title VARCHAR,
    description TEXT,
    remind_at DATETIME,
    status VARCHAR,
    created_at DATETIME,
    updated_at DATETIME
);

-- ========================
-- LOG HTTP
-- ========================
CREATE TABLE IF NOT EXISTS http_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    method VARCHAR,
    url TEXT,
    request_headers TEXT,
    request_body TEXT,
    response_status INTEGER,
    response_body TEXT,
    duration_ms INTEGER,
    status VARCHAR,
    error_message TEXT,
    created_at DATETIME
);

-- ========================
-- POLÍTICAS
-- ========================
CREATE TABLE IF NOT EXISTS policy_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_key VARCHAR,
    value TEXT,
    updated_at DATETIME
);

-- ========================
-- DISPOSITIVO
-- ========================
CREATE TABLE IF NOT EXISTS device (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id VARCHAR NOT NULL,
    device_name VARCHAR,
    os_version VARCHAR,
    is_active INTEGER NOT NULL DEFAULT 0,
    created_at DATETIME,
    last_seen_at DATETIME
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_device_device_id
ON device(device_id);

CREATE UNIQUE INDEX IF NOT EXISTS idx_device_single_active
ON device(is_active)
WHERE is_active = 1;

-- ========================
-- SESIÓN DE AUTENTICACIÓN
-- ========================
CREATE TABLE IF NOT EXISTS auth_session (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    is_logged_in BOOLEAN,
    last_login_at DATETIME,
    last_refresh_at DATETIME,
    user_id VARCHAR,
    user_email VARCHAR,
    user_name VARCHAR,
    avatar_url VARCHAR,
    token_storage VARCHAR,
    token_ref VARCHAR,
    access_token TEXT,
    token_expires_at DATETIME,
    updated_at DATETIME
);

-- ========================
-- HISTORIAL DE COMANDOS
-- ========================
CREATE TABLE IF NOT EXISTS command_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    input_type VARCHAR,
    input_text TEXT,
    ai_provider VARCHAR,
    ai_model VARCHAR,
    ai_action_json TEXT,
    action_type VARCHAR,
    requires_confirmation BOOLEAN,
    user_confirmed BOOLEAN,
    status VARCHAR,
    block_reason VARCHAR,
    error_message TEXT,
    created_at DATETIME,
    executed_at DATETIME
);

-- ========================
-- APPS LOCALES PERMITIDAS
-- ========================
CREATE TABLE IF NOT EXISTS local_apps (
    app_key TEXT PRIMARY KEY,
    friendly_name TEXT NOT NULL,
    category TEXT NOT NULL DEFAULT 'otro',
    executable_path TEXT,           -- ruta completa si se conoce (opcional)
    executable_name TEXT NOT NULL,  -- ej: chrome.exe, winword.exe
    enabled INTEGER NOT NULL DEFAULT 0,
    source TEXT NOT NULL DEFAULT 'seed', -- seed | detected | manual
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS local_app_synonyms (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    app_key TEXT NOT NULL,
    synonym TEXT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(app_key) REFERENCES local_apps(app_key) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_local_app_synonym_unique
ON local_app_synonyms(app_key, synonym);

CREATE INDEX IF NOT EXISTS idx_local_app_synonym
ON local_app_synonyms(synonym);

CREATE INDEX IF NOT EXISTS idx_local_apps_enabled
ON local_apps(enabled);
";

            command.ExecuteNonQuery();

            // Seed de apps permitidas (si no existen)
            SeedLocalApps(connection);
        }
    }
}