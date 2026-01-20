using Anfeta.UI.Models;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading; 


namespace Anfeta.UI.Views
{
    public sealed partial class SearchView : Page
    {

        private readonly LocalPathSearchService _localPathSearch = new();
        private CancellationTokenSource? _cts;
        public ObservableCollection<SearchResultRow> Results { get; } = new();
        public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            BtnSearch_Click(sender, new Microsoft.UI.Xaml.RoutedEventArgs());
        }

        private async void BtnSearch_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var query = SearchBox.Text;

            Results.Clear();

            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            try
            {
                if (ToggleLocal.IsChecked == true)
                {
                    var roots = _localPathSearch.GetDefaultRoots();

                    var found = await _localPathSearch.SearchAsync(
                        query,
                        roots,
                        maxResults: 200,
                        maxDepth: 8,
                        ct: _cts.Token);

                    foreach (var (name, path) in found)
                    {
                        Results.Add(new SearchResultRow
                        {
                            Name = name,
                            Target = path,
                            Source = SearchSource.Local
                        });
                    }
                }

                // Dropbox se conecta después
                StatusText.Text = "Estado: Local listo | Dropbox: pendiente";
            }
            catch (OperationCanceledException)
            {
                // normal si el usuario vuelve a buscar rápido
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

                EmptyResultsHint.Visibility = Results.Count == 0
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;

                CountText.Text = $"{Results.Count} resultados";
            }
        }

        public SearchView()
        {   

            InitializeComponent(); 


            ResultsList.ItemsSource = Results;
            BookmarksList.ItemsSource = Bookmarks;

            // mock visual
            //Results.Add(new SearchResultRow { Name = "Contrato.pdf", Target = @"C:\Docs\Contrato.pdf", Source = SearchSource.Local });
            //Results.Add(new SearchResultRow { Name = "Plan.docx", Target = "https://dropbox/mock/Plan.docx", Source = SearchSource.Dropbox });

            EmptyResultsHint.Visibility = Results.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            CountText.Text = "0 resultados";
        }

    }
}
