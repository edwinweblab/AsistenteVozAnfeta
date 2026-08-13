using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        #region ===== Ayuda V2: comandos actuales =====

        private void MenuHelpV2_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HelpPopup.IsOpen)
            {
                HelpPopup.IsOpen = false;
                return;
            }

            HelpContentHost.Content = BuildHelpV2Content();
            HelpPopup.XamlRoot = XamlRoot;
            HelpPopup.IsOpen = true;
        }

        private void HelpPopupV2Close_Click(
            object sender,
            RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
        }

        private UIElement BuildHelpV2Content()
        {
            var root = new StackPanel
            {
                Spacing = 16,
                Padding = new Thickness(4, 4, 8, 8)
            };

            root.Children.Add(
                BuildHelpV2InfoCard(
                    "Cómo funciona",
                    "Escribe palabras normalmente y ANFETA las combina como AND. " +
                    "También puedes usar operadores, filtros y atajos. Pulsa cualquier comando de esta ayuda para probarlo."));

            root.Children.Add(
                BuildHelpV2Section(
                    "📁 Dropbox · carpetas y archivos",
                    new[]
                    {
                        (".folder", "Atajo nuevo: muestra solo carpetas."),
                        (".carpeta", "Alias en español de .folder."),
                        ("type:folder", "Operador completo: solo carpetas."),
                        (".file", "Atajo nuevo: muestra solo archivos."),
                        (".archivo", "Alias en español de .file."),
                        ("type:file", "Operador completo: solo archivos."),
                        ("ext:pdf", "Solo archivos PDF."),
                        ("ext:pdf;docx;xlsx", "PDF, Word y Excel en una sola búsqueda."),
                        ("folder:finanzas", "Solo resultados cuya ruta contenga 'finanzas'."),
                        ("nopath:SEO", "Excluye resultados cuya ruta contenga 'SEO'.")
                    }));

            root.Children.Add(
                BuildHelpV2Section(
                    "🔎 Búsqueda rápida",
                    new[]
                    {
                        ("factura 2026", "AND automático: deben aparecer ambos términos."),
                        ("\"estado de cuenta\"", "Busca la frase exacta y en ese orden."),
                        ("reporte -SEO", "Busca reporte y excluye SEO."),
                        ("reporte !SEO", "Otra forma de excluir SEO."),
                        ("pdf OR docx", "Cualquiera de los dos términos."),
                        ("a|b|c", "OR compacto entre variantes."),
                        ("( SEO OR ADS ) cliente", "Agrupa condiciones y agrega otro término.")
                    }));

            root.Children.Add(
                BuildHelpV2Section(
                    "🧠 Filtros avanzados",
                    new[]
                    {
                        ("size:>10MB", "Archivos mayores a 10 MB."),
                        ("dm:<=7", "Modificados hace 7 días o menos."),
                        ("date:2026-08-01", "Modificados exactamente en esa fecha."),
                        ("regex:^00act", "Regex: nombre que empieza con 00act."),
                        ("regex:reporte.*(pdf|url)", "Regex: reporte seguido de pdf o url.")
                    }));

            root.Children.Add(
                BuildHelpV2Section(
                    "🗂️ Bases de Notion",
                    new[]
                    {
                        ("revisiones", "Limita la búsqueda a la base Revisiones."),
                        ("zclientes", "Limita a Clientes."),
                        ("zdominios", "Limita a Dominios."),
                        ("zproyectos", "Limita a Programas y proyectos."),
                        ("zcorreos", "Limita a Correos / Contraseñas."),
                        ("zpagar", "Limita a registros de Pagar."),
                        ("zcobrar", "Limita a registros de Cobrar.")
                    }));

            root.Children.Add(
                BuildHelpV2InfoCard(
                    "Tips",
                    "• Puedes combinar filtros:  cliente ext:pdf .file\n" +
                    "• Para carpetas, usa el chip 📁 Carpetas o escribe .folder.\n" +
                    "• La columna Path de Dropbox ahora parte de “Dropbox”, no de C:\\\\…\n" +
                    "• Clic derecho → Abrir en Explorador intenta seleccionar el archivo; si todavía no está local, abre la carpeta Dropbox disponible más cercana."));

            return root;
        }

        private UIElement BuildHelpV2Section(
            string title,
            (string Command, string Description)[] commands)
        {
            var panel = new StackPanel
            {
                Spacing = 7
            };

            panel.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 2, 0, 3)
                });

            foreach (var command in commands)
                panel.Children.Add(BuildHelpV2CommandRow(
                    command.Command,
                    command.Description));

            return panel;
        }

        private UIElement BuildHelpV2CommandRow(
            string command,
            string description)
        {
            var grid = new Grid
            {
                ColumnSpacing = 10
            };

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = GridLength.Auto
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

            var button = new Button
            {
                Content = command,
                MinWidth = 128,
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8)
            };

            ToolTipService.SetToolTip(
                button,
                $"Probar: {command}");

            button.Click += async (_, __) =>
                await RunHelpV2QueryAsync(command);

            var note = new TextBlock
            {
                Text = description,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78,
                FontSize = 11
            };

            Grid.SetColumn(button, 0);
            Grid.SetColumn(note, 1);

            grid.Children.Add(button);
            grid.Children.Add(note);

            return grid;
        }

        private static UIElement BuildHelpV2InfoCard(
            string title,
            string text)
        {
            var panel = new StackPanel
            {
                Spacing = 5
            };

            panel.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                });

            panel.Children.Add(
                new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.8
                });

            return new Border
            {
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Child = panel
            };
        }

        private async Task RunHelpV2QueryAsync(string command)
        {
            var query = (command ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(query))
                return;

            HelpPopup.IsOpen = false;

            _suppressSuggest = true;
            SearchBox.Text = query;
            _suppressSuggest = false;

            SetTabTitle(query);
            NotifyWorkspaceChanged();

            SearchBox.Focus(FocusState.Programmatic);

            if (!App.LocalIndex.HasData)
            {
                StatusText.Text =
                    "Estado: No hay índice cargado para probar el comando.";
                return;
            }

            await RunSearchAsync(query);
        }

        #endregion
    }
}
