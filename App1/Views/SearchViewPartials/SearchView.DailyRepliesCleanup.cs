using Anfeta.UI.Models.Weblab;
using Anfeta.UI.Services.Notion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Limpieza Automática de Respuestas Diarias =====

        /// <summary>
        /// Localiza y elimina a papelera de Notion las respuestas diarias ([RESPUESTA]):
        /// 1. Si ya se encuentra TERMINADA ([TERMINADO]).
        /// 2. Si corresponde al día anterior o más antigua y ya fue procesada/atendida.
        /// Protege en todo momento las respuestas que aún sigan pendientes o en proceso.
        /// </summary>
        public async Task<int> PurgeProcessedAndCompletedRepliesAsync()
        {
            var token = GetSavedNotionToken();
            if (string.IsNullOrWhiteSpace(token))
                return 0;

            var allRows = App.LocalIndex.GetAll();
            var replyRows = allRows
                .Where(r => r.Source == SearchSource.Notion &&
                            !string.IsNullOrWhiteSpace(r.ExternalId) &&
                            (r.DisplayName ?? r.Name ?? string.Empty).Contains("[RESPUESTA]", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (replyRows.Count == 0)
                return 0;

            var today = DateTime.Today;
            var idsToPurge = new List<(SearchResultRow Row, string Reason)>();

            foreach (var row in replyRows)
            {
                var title = row.DisplayName ?? row.Name ?? string.Empty;
                var item = TryCreateMessageViewItem(row);

                bool isTerminado = title.Contains("[TERMINADO]", StringComparison.OrdinalIgnoreCase) ||
                                   (item != null && item.IsCompleted);

                var dateMatch = Regex.Match(title, @"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)");
                DateTime date = DateTime.MinValue;
                bool hasDate = dateMatch.Success && DateTime.TryParse(dateMatch.Value, out date);

                bool isOlderThanToday = hasDate && date.Date < today;

                // Si una respuesta ya se encuentra terminada -> eliminarla automáticamente.
                if (isTerminado)
                {
                    idsToPurge.Add((row, "Respuesta terminada"));
                    continue;
                }

                // Eliminar las respuestas correspondientes al día anterior después de que hayan sido creadas/procesadas.
                // Evitar que el proceso elimine respuestas que todavía estén pendientes o en proceso.
                if (isOlderThanToday)
                {
                    bool isPendingUnread = item != null && item.IsUnread;
                    if (!isPendingUnread)
                    {
                        idsToPurge.Add((row, "Respuesta del día anterior procesada"));
                    }
                }
            }

            if (idsToPurge.Count == 0)
                return 0;

            var service = new NotionPageActionsService();
            var removedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in idsToPurge)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await service.MovePageToTrashAsync(token, target.Row.ExternalId, cts.Token);
                    removedIds.Add(target.Row.ExternalId);
                }
                catch (Exception ex) when (NotionPageActionsService.IsMissingPageError(ex))
                {
                    removedIds.Add(target.Row.ExternalId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PURGE_REPLY_FAIL] {target.Row.ExternalId}: {ex.Message}");
                }
            }

            if (removedIds.Count > 0)
            {
                await RemoveNotionRowsFromIndexAsync(removedIds);
                RefreshMessagesView(force: true);
                RefreshResultsListView();
            }

            return removedIds.Count;
        }

        #endregion
    }
}
