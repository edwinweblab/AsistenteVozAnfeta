using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anfeta.UI.Models.Search;

namespace Anfeta.UI.Views.Dialogs
{
    public sealed partial class SavedSearchFilterDialog : ContentDialog
    {
        public SavedSearchFilter Filter { get; private set; }

        public SavedSearchFilterDialog(SavedSearchFilter? existing = null)
        {
            this.InitializeComponent();

            Filter = existing is null
                ? new SavedSearchFilter()
                : Clone(existing);

            LoadFromFilter(Filter);

            PrimaryButtonClick += OnPrimaryButtonClick;
        }

        private void LoadFromFilter(SavedSearchFilter filter)
        {
            NameTextBox.Text = filter.Name ?? string.Empty;
            DescriptionTextBox.Text = filter.Description ?? string.Empty;
            QueryTextBox.Text = filter.Query ?? string.Empty;

            MatchCaseCheckBox.IsChecked = filter.MatchCase;
            
            MatchPathCheckBox.IsChecked = filter.MatchPath;
            

            

            SelectSortBy(filter.SortBy);
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string name = (NameTextBox.Text ?? string.Empty).Trim();
            string query = (QueryTextBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(query))
            {
                args.Cancel = true;
                return;
            }

            Filter.Name = name;
            Filter.Description = (DescriptionTextBox.Text ?? string.Empty).Trim();
            Filter.Query = query;

            Filter.MatchCase = MatchCaseCheckBox.IsChecked == true;
            
            Filter.MatchPath = MatchPathCheckBox.IsChecked == true;
            

            

            Filter.SortBy = GetSelectedSortByTag();
        }

        private void SelectSortBy(string? sortBy)
        {
            string target = string.IsNullOrWhiteSpace(sortBy) ? "name_asc" : sortBy;

            foreach (var item in SortByComboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    SortByComboBox.SelectedItem = item;
                    return;
                }
            }

            SortByComboBox.SelectedIndex = 0;
        }

        private string GetSelectedSortByTag()
        {
            if (SortByComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                !string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }

            return "name_asc";
        }

        private static SavedSearchFilter Clone(SavedSearchFilter source)
        {
            return new SavedSearchFilter
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                Query = source.Query,

                MatchCase = source.MatchCase,
                MatchWholeWord = source.MatchWholeWord,
                MatchPath = source.MatchPath,
                UseRegex = source.UseRegex,

                MatchPrefix = source.MatchPrefix,
                MatchSuffix = source.MatchSuffix,
                IgnorePunctuation = source.IgnorePunctuation,
                IgnoreWhitespace = source.IgnoreWhitespace,
                MatchDiacritics = source.MatchDiacritics,

                SortBy = source.SortBy,
                IsPinned = source.IsPinned,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt
            };
        }
    }
}