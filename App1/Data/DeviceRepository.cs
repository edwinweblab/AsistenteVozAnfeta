using System;

namespace Anfeta.UI.Data
{
    public static class DeviceRepository
    {
        /// <summary>
        /// Garantiza que exista un device activo.
        /// Si no hay uno, lo crea y lo marca como activo.
        /// Devuelve el device_id activo.
        /// </summary>
        public static string EnsureActiveDevice()
        {
            using var connection = DbConnectionFactory.Create();
            connection.Open();

            // 1. Buscar device activo
            using (var findCmd = connection.CreateCommand())
            {
                findCmd.CommandText = @"
                    SELECT device_id
                    FROM device
                    WHERE is_active = 1
                    LIMIT 1;
                ";

                var existing = findCmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    // Actualizar last_seen_at
                    using var updateSeen = connection.CreateCommand();
                    updateSeen.CommandText = @"
                        UPDATE device
                        SET last_seen_at = CURRENT_TIMESTAMP
                        WHERE device_id = @deviceId;
                    ";
                    updateSeen.Parameters.AddWithValue("@deviceId", existing);
                    updateSeen.ExecuteNonQuery();

                    return existing;
                }
            }

            // 2. No existe device activo → crear uno nuevo
            var newDeviceId = Guid.NewGuid().ToString("N");

            using (var tx = connection.BeginTransaction())
            {
                // Desactivar cualquiera que exista
                using (var deactivateCmd = connection.CreateCommand())
                {
                    deactivateCmd.CommandText = @"
                        UPDATE device
                        SET is_active = 0;
                    ";
                    deactivateCmd.ExecuteNonQuery();
                }

                // Insertar nuevo device activo
                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT INTO device (
                            device_id,
                            device_name,
                            os_version,
                            is_active,
                            created_at,
                            last_seen_at
                        )
                        VALUES (
                            @deviceId,
                            @deviceName,
                            @osVersion,
                            1,
                            CURRENT_TIMESTAMP,
                            CURRENT_TIMESTAMP
                        );
                    ";

                    insertCmd.Parameters.AddWithValue("@deviceId", newDeviceId);
                    insertCmd.Parameters.AddWithValue("@deviceName", Environment.MachineName);
                    insertCmd.Parameters.AddWithValue("@osVersion", Environment.OSVersion.VersionString);

                    insertCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            return newDeviceId;
        }
    }
}
