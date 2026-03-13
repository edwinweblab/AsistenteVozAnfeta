using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anfeta.UI.Models.Search;

namespace Anfeta.UI.Services.Search
{
    public sealed class EverythingCsvFilterImporter
    {
        public async Task<List<SavedSearchFilter>> ImportFromFileAsync(
            string filePath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath vacío.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException("No existe el archivo CSV.", filePath);

            var lines = await File.ReadAllLinesAsync(filePath, ct);
            var result = new List<SavedSearchFilter>();

            if (lines.Length == 0)
                return result;

            var header = SplitCsvLine(lines[0]);
            if (header.Count == 0)
                return result;

            for (int i = 1; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var values = SplitCsvLine(line);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (int c = 0; c < header.Count; c++)
                {
                    var key = header[c]?.Trim() ?? string.Empty;
                    var value = c < values.Count ? values[c] : string.Empty;
                    row[key] = value?.Trim() ?? string.Empty;
                }

                var filter = MapRow(row);
                if (filter is not null)
                    result.Add(filter);
            }

            return result;
        }

        private SavedSearchFilter? MapRow(Dictionary<string, string> row)
        {
            string name = Get(row, "Name");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string query = Get(row, "Search");

            return new SavedSearchFilter
            {
                Name = name.Trim(),
                Description = "Importado desde CSV",
                Query = query?.Trim() ?? string.Empty,

                MatchCase = ToBool01(Get(row, "Case")),
                MatchWholeWord = ToBool01(Get(row, "Whole Word")),
                MatchPath = ToBool01(Get(row, "Path")),
                UseRegex = ToBool01(Get(row, "Regex")),

                SortBy = NormalizeSort(Get(row, "Sort")),
                IsPinned = false
            };
        }

        private static string Get(Dictionary<string, string> row, string key)
        {
            return row.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }

        private static bool ToBool01(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();

            return value == "1" ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSort(string? sort)
        {
            sort = (sort ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(sort))
                return "name_asc";

            return sort.ToLowerInvariant() switch
            {
                "name" => "name_asc",
                "path" => "path_asc",
                "size" => "size_desc",
                "date-modified" => "modified_desc",
                "dm" => "modified_desc",
                _ => "name_asc"
            };
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            if (line is null)
                return result;

            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            result.Add(current.ToString());
            return result;
        }
    }
}