using System.Collections.Generic;

namespace Anfeta.UI.Services.Notion
{
    public static class NotionDataSources
    {
        public static IReadOnlyList<NotionDataSourceConfig> Default { get; } =
            new List<NotionDataSourceConfig>
            {
                new()
                {
                    Name = "Revisiones",
                    DataSourceId = "2eeabd7d-91b7-8193-a131-000b08cd54e2",
                    Enabled = true
                },
                new()
                {
                    Name = "Clientes",
                    DataSourceId = "2f0abd7d-91b7-810b-b1b8-000b85005b68",
                    Enabled = true
                },
                new()
                {
                    Name = "Dominios",
                    DataSourceId = "2f0abd7d-91b7-8133-82e8-000b57bceeda",
                    Enabled = true
                },
                new()
                {
                    Name = "Programas y proyectos",
                    DataSourceId = "796abd7d-91b7-8271-9154-0749f7d58213",
                    Enabled = true
                },
                new()
                {
                    Name = "Cobrar y pagar",
                    DataSourceId = "2eeabd7d-91b7-815f-b649-000bfdc93a2b",
                    Enabled = true
                },
                new()
                {
                    Name = "Correos Contraseñas",
                    DataSourceId = "2f1abd7d-91b7-8136-8cc2-000ba807c666",
                    Enabled = true
                }
            };
    }
}