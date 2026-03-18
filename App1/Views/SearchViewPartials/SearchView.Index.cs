using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Speech;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using static Anfeta.UI.Helpers.AppSettingsKeys;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Index Coordinator (Auto-index) =====

        private void OnIndexStateChanged()
        {
            DispatcherQueue.TryEnqueue(() => { _ = ApplyIndexStateAsync(); });
        }

        private async Task ApplyIndexStateAsync()
        {
            if (DropboxIndexCoordinator.IsIndexing)
            {
                ResetSearchModuleState();
                StatusText.Text = "Estado: Ruta nueva detectada, indexando...";
                return;
            }

            if (!string.IsNullOrWhiteSpace(DropboxIndexCoordinator.LastError))
            {
                ResetSearchModuleState();
                StatusText.Text = $"Estado: Error indexando -> {DropboxIndexCoordinator.LastError}";
                return;
            }

            if (DropboxIndexCoordinator.IsReady && App.LocalIndex.HasData)
            {
                var root = DropboxIndexCoordinator.RootPath ?? "";

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado: Ruta invalida. Configura de nuevo en Settings.";
                    return;
                }

                ResetSearchModuleState();

                DROPBOX_ROOT = root;
                _currentFolder = "";
                _currentFolderPath = DROPBOX_ROOT;
                _backStack.Clear();
                _forwardStack.Clear();

                LoadFoldersRoot();
                BuildTreeRoot();

                await BrowseFolderAsync(DROPBOX_ROOT, pushHistory: false);

                StatusText.Text = $"Estado: Index local listo ({App.LocalIndex.Count} items)";
            }
        }

        private async Task EnsureIndexBootstrappedAsync()
        {
            await _bootstrapLock.WaitAsync();
            try
            {
                if (_bootstrappedOnce && App.LocalIndex.HasData)
                    return;

                var saved = ApplicationData.Current.LocalSettings.Values[LS_DropboxRoot] as string;
                var savedRoot = (saved ?? "").Trim();

                if (_bootstrappedOnce &&
                    App.LocalIndex.HasData &&
                    !string.IsNullOrWhiteSpace(savedRoot) &&
                    string.Equals(DROPBOX_ROOT, savedRoot, StringComparison.OrdinalIgnoreCase))
                    return;

                // Si no hay ruta configurada, salir limpiamente sin intentar .Trim() sobre null
                if (string.IsNullOrWhiteSpace(saved))
                {
                    ResetSearchModuleState();
                    StatusText.Text = "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                    _bootstrappedOnce = true;
                    return;
                }

                DROPBOX_ROOT = saved.Trim();

                if (!App.LocalIndex.HasData && !DropboxIndexCoordinator.IsIndexing)
                {
                    var (ok, cachedRoot, items) = await LocalIndexPersistence.TryLoadAsync(CancellationToken.None);

                    var cacheMatchesRoot =
                        ok &&
                        !string.IsNullOrWhiteSpace(cachedRoot) &&
                        string.Equals(cachedRoot.Trim(), DROPBOX_ROOT, StringComparison.OrdinalIgnoreCase) &&
                        LocalIndexPersistence.RootExists(DROPBOX_ROOT) &&
                        items != null && items.Count > 0;

                    if (cacheMatchesRoot)
                    {
                        App.LocalIndex.Set(items);
                        DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    }
                }

                if (App.LocalIndex.HasData && !DropboxIndexCoordinator.IsIndexing)
                {
                    var lastIndexedStr = ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] as string;

                    DateTimeOffset? lastIndexedUtc = null;
                    if (!string.IsNullOrWhiteSpace(lastIndexedStr) &&
                        DateTimeOffset.TryParse(lastIndexedStr, out var parsed))
                        lastIndexedUtc = parsed.ToUniversalTime();

                    var folderLastWriteUtc = Directory.GetLastWriteTimeUtc(DROPBOX_ROOT);
                    var shouldReindex = lastIndexedUtc == null ||
                                        folderLastWriteUtc > lastIndexedUtc.Value.UtcDateTime;

                    if (shouldReindex)
                        await ReindexCurrentRootAsync();
                }

                if (DropboxIndexCoordinator.IsIndexing || !App.LocalIndex.HasData)
                {
                    ResetSearchModuleState();
                    StatusText.Text = DropboxIndexCoordinator.IsIndexing
                        ? "Estado: Ruta nueva detectada, indexando..."
                        : "Estado: No hay índice cargado. Ve a Settings y selecciona la ruta para indexar.";
                    _bootstrappedOnce = true;
                    return;
                }

                LoadFoldersRoot();
                BuildTreeRoot();

                var startFolder = (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
                    ? _currentFolderPath
                    : DROPBOX_ROOT;

                await BrowseFolderAsync(startFolder, pushHistory: false);

                CommandsSidebarList.ItemsSource = _savedSearches;
                RefreshCommandsSidebarUi();

                StatusText.Text = $"Estado: Index local listo ({App.LocalIndex.Count} items)";
                _bootstrappedOnce = true;
            }
            finally
            {
                _bootstrapLock.Release();
            }
        }

        private async Task ReindexCurrentRootAsync()
        {
            if (string.IsNullOrWhiteSpace(DROPBOX_ROOT) || !Directory.Exists(DROPBOX_ROOT))
                return;

            try
            {
                _autoReindexCts?.Cancel();
                _autoReindexCts = new CancellationTokenSource();
                var ct = _autoReindexCts.Token;

                StatusText.Text = "Estado: Detecté cambios en la carpeta. Reindexando...";
                DropboxIndexCoordinator.StartIndexing(DROPBOX_ROOT);
                App.LocalIndex.Clear();

                var list = await LocalIndexBuilder.BuildAsync(DROPBOX_ROOT, ct);

                if (list == null || list.Count == 0)
                {
                    StatusText.Text = "Estado: Reindex produjo 0 items. Conservo el índice anterior.";
                    DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                    return;
                }

                App.LocalIndex.Set(list);
                await LocalIndexPersistence.SaveAsync(DROPBOX_ROOT, list, ct);

                ApplicationData.Current.LocalSettings.Values[LS_LastIndexedUtc] =
                    DateTimeOffset.UtcNow.ToString("O");

                DropboxIndexCoordinator.MarkReady(DROPBOX_ROOT);
                StatusText.Text = $"Estado: Reindex listo ({App.LocalIndex.Count} items)";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DropboxIndexCoordinator.MarkError(DROPBOX_ROOT, ex.Message);
                StatusText.Text = $"Estado: Error reindexando -> {ex.Message}";
            }
        }

        #endregion
    }
}