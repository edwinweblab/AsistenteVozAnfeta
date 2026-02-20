using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;


namespace Anfeta.UI.Data
{
    public class ApiKeyRow
    {
        public long Id { get; set; }
        public string Provider { get; set; } = "groq";
        public string? Name { get; set; }
        public string ApiKey { get; set; } = "";
        public bool IsActive { get; set; }
        public string? LastValidatedAt { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }

        // NUEVO: para habilitar el botón "Activar" solo cuando NO es activa
        public bool CanActivate => !IsActive;

        public string Masked => string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length < 8
            ? "(oculta)"
            : $"{ApiKey.Substring(0, 4)}...{ApiKey.Substring(ApiKey.Length - 4)}";

        public string ActiveText => IsActive ? "ACTIVA" : "INACTIVA";

        public Visibility ActiveVisibility =>
            IsActive ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InactiveVisibility =>
            IsActive ? Visibility.Collapsed : Visibility.Visible;
    }



    public class ApiKeyRepository
    {
        public async Task<List<ApiKeyRow>> GetAllAsync(string provider = "groq")
        {
            var list = new List<ApiKeyRow>();

            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, provider, name, api_key, is_active, last_validated_at, created_at, updated_at
FROM api_keys
WHERE provider = $p
ORDER BY is_active DESC, id DESC;";
            cmd.Parameters.AddWithValue("$p", provider);

            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new ApiKeyRow
                {
                    Id = r.GetInt64(0),
                    Provider = r.GetString(1),
                    Name = r.IsDBNull(2) ? null : r.GetString(2),
                    ApiKey = r.GetString(3),
                    IsActive = !r.IsDBNull(4) && r.GetInt32(4) == 1,
                    LastValidatedAt = r.IsDBNull(5) ? null : r.GetString(5),
                    CreatedAt = r.IsDBNull(6) ? null : r.GetString(6),
                    UpdatedAt = r.IsDBNull(7) ? null : r.GetString(7)
                });
            }

            return list;
        }

        public async Task<(long? id, string? apiKey)> GetActiveAsync(string provider)
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, api_key
FROM api_keys
WHERE provider = $p AND is_active = 1
LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", provider);

            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (null, null);

            return (r.GetInt64(0), r.GetString(1));
        }

        public async Task<long> InsertAsync(string provider, string? name, string apiKey, bool makeActive, string? validatedAtIso = null)
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();

            var now = DateTime.UtcNow.ToString("o");

            if (makeActive)
            {
                var off = conn.CreateCommand();
                off.Transaction = tx;
                off.CommandText = @"UPDATE api_keys SET is_active = 0, updated_at = $u WHERE provider = $p;";
                off.Parameters.AddWithValue("$p", provider);
                off.Parameters.AddWithValue("$u", now);
                await off.ExecuteNonQueryAsync();
            }

            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO api_keys(provider, name, api_key, is_active, last_validated_at, created_at, updated_at)
VALUES($p, $n, $k, $a, $v, $c, $u);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", apiKey);
            cmd.Parameters.AddWithValue("$a", makeActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", (object?)validatedAtIso ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", now);
            cmd.Parameters.AddWithValue("$u", now);

            var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

            await tx.CommitAsync();
            return id;
        }

        public async Task SetActiveAsync(long id, string provider = "groq")
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            var now = DateTime.UtcNow.ToString("o");

            var off = conn.CreateCommand();
            off.Transaction = tx;
            off.CommandText = @"UPDATE api_keys SET is_active = 0, updated_at = $u WHERE provider = $p;";
            off.Parameters.AddWithValue("$p", provider);
            off.Parameters.AddWithValue("$u", now);
            await off.ExecuteNonQueryAsync();

            var on = conn.CreateCommand();
            on.Transaction = tx;
            on.CommandText = @"UPDATE api_keys SET is_active = 1, updated_at = $u WHERE id = $id AND provider = $p;";
            on.Parameters.AddWithValue("$id", id);
            on.Parameters.AddWithValue("$p", provider);
            on.Parameters.AddWithValue("$u", now);
            await on.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        public async Task DeleteAsync(long id)
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM api_keys WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UpdateNameAsync(long id, string? name)
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE api_keys SET name = $n, updated_at = $u WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UpdateValidatedAtAsync(long id)
        {
            using var conn = DbConnectionFactory.Create();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE api_keys SET last_validated_at = $v, updated_at = $u WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
