using Anfeta.UI.Data;
using Anfeta.UI.Models.Notion;
using Anfeta.UI.Models.Weblab;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Anfeta.UI.Services.Notion
{
    public sealed class NotionCachedContent
    {
        public string PageId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ContentText { get; set; } = string.Empty;
        public DateTimeOffset? LastEditedUtc { get; set; }
        public DateTimeOffset CachedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NotionContentMatch
    {
        public SearchResultRow Row { get; set; } = null!;
        public string Snippet { get; set; } = string.Empty;
        public string MatchedTerm { get; set; } = string.Empty;
    }

    public sealed class NotionContentIndexService
    {
        private const string CacheFileName = "notion_content_cache.json";
        private readonly ConcurrentDictionary<string, NotionCachedContent> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly SemaphoreSlim _persistGate = new(1, 1);
        private readonly NotionPagePreviewService _previewService = new();
        private bool _isInitialized;
        private bool _isDirty;

        public int CachedPagesCount => _cache.Count;

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await _gate.WaitAsync();
            try
            {
                if (_isInitialized) return;

                // 1. Cargar desde SQLite (notion_page_content)
                await Task.Run(() =>
                {
                    try
                    {
                        using var connection = DbConnectionFactory.Create();
                        connection.Open();

                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT page_id, title, content_text, last_edited_utc, cached_at_utc FROM notion_page_content;";

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var pageId = reader.GetString(0);
                            var title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            var content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                            DateTimeOffset? lastEdited = null;
                            if (!reader.IsDBNull(3) && DateTimeOffset.TryParse(reader.GetString(3), out var le))
                                lastEdited = le;
                            DateTimeOffset cachedAt = DateTimeOffset.UtcNow;
                            if (!reader.IsDBNull(4) && DateTimeOffset.TryParse(reader.GetString(4), out var ca))
                                cachedAt = ca;

                            _cache[pageId] = new NotionCachedContent
                            {
                                PageId = pageId,
                                Title = title,
                                ContentText = content,
                                LastEditedUtc = lastEdited,
                                CachedAtUtc = cachedAt
                            };
                        }
                    }
                    catch { }
                });

                // 2. Si SQLite estaba vacío, migración fallback desde notion_content_cache.json
                if (_cache.IsEmpty)
                {
                    var folder = ApplicationData.Current.LocalFolder;
                    var item = await folder.TryGetItemAsync(CacheFileName);
                    if (item is StorageFile file)
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var entries = JsonSerializer.Deserialize<List<NotionCachedContent>>(json);
                            if (entries != null)
                            {
                                foreach (var entry in entries)
                                {
                                    if (!string.IsNullOrWhiteSpace(entry.PageId))
                                    {
                                        _cache[entry.PageId] = entry;
                                        StoreContent(entry.PageId, entry.Title, entry.ContentText, entry.LastEditedUtc);
                                    }
                                }
                            }
                        }
                    }
                }

                _isInitialized = true;
            }
            catch
            {
                _isInitialized = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(CancellationToken ct = default)
        {
            if (!_isDirty && _isInitialized) return;

            await _persistGate.WaitAsync(ct);
            try
            {
                var list = _cache.Values.ToList();
                await Task.Run(() =>
                {
                    using var connection = DbConnectionFactory.Create();
                    connection.Open();

                    using var tx = connection.BeginTransaction();
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR REPLACE INTO notion_page_content (page_id, title, content_text, last_edited_utc, cached_at_utc)
VALUES (@id, @title, @content, @edited, @cached);";

                    var pId = cmd.Parameters.Add("@id", SqliteType.Text);
                    var pTitle = cmd.Parameters.Add("@title", SqliteType.Text);
                    var pContent = cmd.Parameters.Add("@content", SqliteType.Text);
                    var pEdited = cmd.Parameters.Add("@edited", SqliteType.Text);
                    var pCached = cmd.Parameters.Add("@cached", SqliteType.Text);

                    foreach (var entry in list)
                    {
                        pId.Value = entry.PageId;
                        pTitle.Value = entry.Title ?? string.Empty;
                        pContent.Value = entry.ContentText ?? string.Empty;
                        pEdited.Value = entry.LastEditedUtc.HasValue ? entry.LastEditedUtc.Value.ToString("O") : (object)DBNull.Value;
                        pCached.Value = entry.CachedAtUtc.ToString("O");
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }, ct);

                _isDirty = false;
            }
            catch
            {
                // Silently ignore disk errors to avoid disrupting search UI
            }
            finally
            {
                _persistGate.Release();
            }
        }

        public bool TryGetContent(string pageId, out string content)
        {
            if (string.IsNullOrWhiteSpace(pageId))
            {
                content = string.Empty;
                return false;
            }

            var normalized = pageId.Replace("-", string.Empty).Trim();
            if (_cache.TryGetValue(normalized, out var entry) || _cache.TryGetValue(pageId, out entry))
            {
                content = entry.ContentText;
                return !string.IsNullOrWhiteSpace(content);
            }

            content = string.Empty;
            return false;
        }

        public void StoreContent(string pageId, string title, string contentText, DateTimeOffset? lastEditedUtc)
        {
            if (string.IsNullOrWhiteSpace(pageId)) return;

            var normalized = pageId.Replace("-", string.Empty).Trim();
            var entry = new NotionCachedContent
            {
                PageId = normalized,
                Title = title ?? string.Empty,
                ContentText = contentText ?? string.Empty,
                LastEditedUtc = lastEditedUtc,
                CachedAtUtc = DateTimeOffset.UtcNow
            };

            _cache[normalized] = entry;
            _isDirty = true;

            // Persistencia inmediata no bloqueante en SQLite
            _ = Task.Run(() =>
            {
                try
                {
                    using var connection = DbConnectionFactory.Create();
                    connection.Open();

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
INSERT OR REPLACE INTO notion_page_content (page_id, title, content_text, last_edited_utc, cached_at_utc)
VALUES (@id, @title, @content, @edited, @cached);";
                    cmd.Parameters.AddWithValue("@id", normalized);
                    cmd.Parameters.AddWithValue("@title", entry.Title);
                    cmd.Parameters.AddWithValue("@content", entry.ContentText);
                    cmd.Parameters.AddWithValue("@edited", entry.LastEditedUtc.HasValue ? entry.LastEditedUtc.Value.ToString("O") : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@cached", entry.CachedAtUtc.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
                catch { }
            });
        }

        public async Task<string> GetOrFetchContentAsync(
            string token,
            string pageId,
            string title,
            DateTimeOffset? lastEditedUtc,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(pageId)) return string.Empty;

            var normalized = pageId.Replace("-", string.Empty).Trim();
            if (_cache.TryGetValue(normalized, out var entry))
            {
                if (entry.LastEditedUtc.HasValue && lastEditedUtc.HasValue &&
                    entry.LastEditedUtc.Value >= lastEditedUtc.Value &&
                    !string.IsNullOrWhiteSpace(entry.ContentText))
                {
                    return entry.ContentText;
                }
            }

            try
            {
                var blocks = await _previewService.GetPagePreviewAsync(token, pageId, ct);
                var text = ExtractTextFromBlocks(blocks);
                StoreContent(normalized, title, text, lastEditedUtc);
                return text;
            }
            catch
            {
                return entry?.ContentText ?? string.Empty;
            }
        }

        public static string ExtractTextFromBlocks(IEnumerable<NotionPreviewBlock>? blocks)
        {
            if (blocks == null) return string.Empty;

            var lines = new List<string>();
            foreach (var b in blocks)
            {
                if (!string.IsNullOrWhiteSpace(b.Text))
                    lines.Add(b.Text.Trim());
                if (!string.IsNullOrWhiteSpace(b.Caption))
                    lines.Add(b.Caption.Trim());
            }

            return string.Join(" \n ", lines);
        }

        public async Task IndexPendingPagesAsync(
            IEnumerable<SearchResultRow> rows,
            string token,
            IProgress<(int current, int total)>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            await InitializeAsync();

            var notionRows = rows
                .Where(r => r.Source == SearchSource.Notion && !string.IsNullOrWhiteSpace(r.ExternalId))
                .ToList();

            if (notionRows.Count == 0) return;

            // Priorizar las páginas que no están en caché o están desactualizadas
            var toFetch = notionRows
                .Where(r =>
                {
                    var id = (r.ExternalId ?? "").Replace("-", string.Empty).Trim();
                    if (!_cache.TryGetValue(id, out var entry)) return true;
                    if (r.NotionEditedUtc.HasValue && entry.LastEditedUtc.HasValue &&
                        r.NotionEditedUtc.Value > entry.LastEditedUtc.Value) return true;
                    return string.IsNullOrWhiteSpace(entry.ContentText);
                })
                .ToList();

            var total = toFetch.Count;
            var current = 0;

            // Limitar concurrencia a 3 para respetar rate limits de Notion (3 req/sec)
            using var throttle = new SemaphoreSlim(3, 3);
            var tasks = toFetch.Select(async row =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await GetOrFetchContentAsync(token, row.ExternalId, row.Name, row.NotionEditedUtc, ct);
                }
                catch
                {
                    // Ignorar errores puntuales por página
                }
                finally
                {
                    var done = Interlocked.Increment(ref current);
                    progress?.Report((done, total));
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);
            await SaveAsync(CancellationToken.None);
        }

        public List<NotionContentMatch> SearchContent(
            string query,
            IEnumerable<SearchResultRow> candidateRows)
        {
            var results = new List<NotionContentMatch>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            var cleanQuery = query.Trim();
            var terms = cleanQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 2)
                .ToList();

            if (terms.Count == 0) terms.Add(cleanQuery);

            foreach (var row in candidateRows)
            {
                if (row.Source != SearchSource.Notion || string.IsNullOrWhiteSpace(row.ExternalId))
                    continue;

                var id = row.ExternalId.Replace("-", string.Empty).Trim();
                if (!_cache.TryGetValue(id, out var entry) || string.IsNullOrWhiteSpace(entry.ContentText))
                    continue;

                // Validar si todos los términos o el query completo coinciden en el cuerpo
                var matched = false;
                string firstMatchedTerm = cleanQuery;

                if (entry.ContentText.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    firstMatchedTerm = cleanQuery;
                }
                else if (terms.Count > 1 && terms.All(t => entry.ContentText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = true;
                    firstMatchedTerm = terms[0];
                }

                if (matched)
                {
                    var snippet = ExtractSnippet(entry.ContentText, firstMatchedTerm);
                    results.Add(new NotionContentMatch
                    {
                        Row = row,
                        Snippet = snippet,
                        MatchedTerm = firstMatchedTerm
                    });
                }
            }

            return results;
        }

        public static string ExtractSnippet(string content, string term, int contextLength = 55)
        {
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(term))
                return string.Empty;

            var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return string.Empty;

            var start = Math.Max(0, index - contextLength);
            var end = Math.Min(content.Length, index + term.Length + contextLength);
            var length = end - start;

            var snippet = content.Substring(start, length).Replace("\n", " ").Replace("\r", " ").Trim();
            var prefix = start > 0 ? "... " : string.Empty;
            var suffix = end < content.Length ? " ..." : string.Empty;

            return $"{prefix}{snippet}{suffix}";
        }
    }
}
