using Microsoft.Data.Sqlite;

namespace App1.Data
{
    public static class DbConnectionFactory
    {
        public static SqliteConnection Create()
        {
            var dbPath = DatabaseInitializer.GetDatabasePath();
            return new SqliteConnection($"Data Source={dbPath}");
        }
    }
}
