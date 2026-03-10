// Data/CommandHistoryRepository.cs
using Anfeta.UI.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Anfeta.UI.Data
{
    public class CommandHistoryRepository
    {
        private readonly string _dbPath;

        public CommandHistoryRepository()
        {
            _dbPath = DatabaseInitializer.GetDatabasePath();
        }

        // Inserta un comando ejecutado en la tabla command_history.
        // Input: texto reconocido, categoría, fecha/hora de ejecución.
        public async Task InsertAsync(string inputText, string category, DateTime createdAt)
        {
            await Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO command_history (input_text, action_type, created_at)
                    VALUES ($input, $category, $created);
                ";
                cmd.Parameters.AddWithValue("$input", inputText);
                cmd.Parameters.AddWithValue("$category", category);
                cmd.Parameters.AddWithValue("$created", createdAt.ToString("o"));
                cmd.ExecuteNonQuery();
            });
        }

        // Obtiene los N comandos más recientes ordenados del más nuevo al más viejo.
        // Input: cantidad máxima a retornar. Output: lista de VoiceHistoryEntry.
        public async Task<List<VoiceHistoryEntry>> GetRecentAsync(int count = 15)
        {
            return await Task.Run(() =>
            {
                var result = new List<VoiceHistoryEntry>();

                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT input_text, action_type, created_at
                    FROM command_history
                    ORDER BY id DESC
                    LIMIT $count;
                ";
                cmd.Parameters.AddWithValue("$count", count);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var inputText = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var category = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var createdAt = reader.IsDBNull(2) ? "" : reader.GetString(2);

                    var time = "";
                    if (DateTime.TryParse(createdAt, out var dt))
                        time = dt.ToLocalTime().ToString("HH:mm");

                    result.Add(new VoiceHistoryEntry
                    {
                        InputText = inputText,
                        Category = category,
                        Time = time
                    });
                }

                return result;
            });
        }

        // Cuenta los comandos ejecutados hoy (hora local).
        // Output: número de comandos del día actual.
        public async Task<int> GetTodayCountAsync()
        {
            return await Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*)
                    FROM command_history
                    WHERE DATE(created_at, 'localtime') = DATE('now', 'localtime');
                ";

                var result = cmd.ExecuteScalar();
                return result is long count ? (int)count : 0;
            });
        }
    }
}