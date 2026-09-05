using System;
using System.Linq;
using System.Threading;
using Anfeta.UI.Services.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Security.Credentials;

namespace Anfeta.UI.Views;

public sealed partial class SearchView
{
    private bool _dailySummaryOpen;
    private const string SummaryCredential = "ANFETA.OpenAI.Summary";

    private async void DailyAiSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_dailySummaryOpen) return;
        _dailySummaryOpen = true;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var activities = _activeTodayProjectSource.Where(x => !x.IsReviewMirror && x.Start.Date == DateTime.Today)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.PageId) ? x.Title + x.Start.ToString("O") : x.PageId.Replace("-", ""), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
            var phases = activities.Select(x => GetCalendarProjectPhaseInfo(x)?.Token ?? "").ToList();
            var known = activities.Where(x => x.ChecklistScanned).ToList();
            var pending = phases.Count(x => x == "prtuzREVISION" || x == "aprtuzREVISION");
            var review = phases.Count(x => x == "rtuzREVISION");
            var suspended = phases.Count(x => x == "sprtuzREVISION");
            var finished = phases.Count(x => x == "zREVISION");
            var counts = new DailySummaryCounts(activities.Count, pending, review, suspended, finished,
                activities.Count - pending - review - suspended - finished, known.Count,
                known.Sum(x => Math.Clamp(x.ChecklistCompleted, 0, Math.Max(0, x.ChecklistTotal))),
                known.Sum(x => Math.Max(0, x.ChecklistTotal)));
            var panel = new StackPanel { Spacing = 10 };
            TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(Label($"Hoy {DateTime.Today:dd/MM/yyyy} · Instantánea {DateTime.Now:HH:mm}\n" +
                $"{counts.Activities} actividades cargadas, sin duplicar páginas ni espejos de revisión.\n" +
                $"Pendientes/por hacer: {pending} · En revisión: {review} · Suspendidas: {suspended} · Terminadas: {finished} · Otros: {counts.Other}\n" +
                $"Checklist consultado: {known.Count}/{activities.Count} actividades; {counts.ChecksDone}/{counts.ChecksTotal} subtareas completadas.\n" +
                "No representa avance en horas ni el historial completo de los proyectos. No aplica el filtro visual de estados."));
            panel.Children.Add(Label("OpenAI opcional: se enviarán únicamente estos conteos, sin nombres, títulos, enlaces ni comentarios. " +
                "Usa tu propia clave; la API tiene facturación independiente de ChatGPT. No se realizan cambios en Notion. Modelo: " + DailyAiSummaryService.Model));
            var keyBox = new PasswordBox { Header = "Clave de OpenAI (solo si necesitas configurarla o cambiarla)", PlaceholderText = "No pegues la clave en el chat" };
            var remember = new CheckBox { Content = "Guardar mi clave en el almacén de credenciales de Windows", IsChecked = false };
            var forget = new Button { Content = "Borrar clave guardada" };
            var consent = new CheckBox { Content = new TextBlock { Text = "Autorizo enviar estos conteos a OpenAI y el consumo de mi cuenta API.", TextWrapping = TextWrapping.Wrap } };
            var generate = new Button { Content = "Generar resumen IA", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            var result = Label("El resumen local está disponible sin API.");
            var vault = new PasswordVault();
            try { vault.Retrieve(SummaryCredential, "user"); result.Text = "Hay una clave guardada; no se enviará nada hasta que autorices y pulses Generar."; } catch { }
            consent.Checked += (_, _) => generate.IsEnabled = activities.Count > 0;
            consent.Unchecked += (_, _) => generate.IsEnabled = false;
            forget.Click += (_, _) =>
            {
                try { vault.Remove(vault.Retrieve(SummaryCredential, "user")); result.Text = "Clave guardada eliminada."; }
                catch { result.Text = "No hay una clave guardada disponible."; }
                keyBox.Password = "";
            };
            generate.Click += async (_, _) =>
            {
                generate.IsEnabled = false;
                consent.IsEnabled = false;
                try
                {
                    var key = keyBox.Password.Trim();
                    if (key.Length == 0)
                    {
                        try { var credential = vault.Retrieve(SummaryCredential, "user"); credential.RetrievePassword(); key = credential.Password; }
                        catch { throw new InvalidOperationException("Configura tu clave de OpenAI en este campo."); }
                    }
                    if (remember.IsChecked == true) vault.Add(new PasswordCredential(SummaryCredential, "user", key));
                    keyBox.Password = "";
                    result.Text = "Generando… Puedes cerrar para cancelar.";
                    result.Text = "Resumen IA — verifica las sugerencias:\n\n" + await new DailyAiSummaryService().GenerateAsync(key, counts, cancellation.Token);
                }
                catch (OperationCanceledException) { result.Text = "Solicitud cancelada o tiempo de espera agotado."; }
                catch (InvalidOperationException ex) { result.Text = ex.Message; }
                catch { result.Text = "No se pudo obtener un resumen válido. Revisa tu conexión; los datos locales siguen disponibles."; }
                finally { consent.IsEnabled = true; generate.IsEnabled = consent.IsChecked == true && activities.Count > 0; }
            };
            panel.Children.Add(keyBox); panel.Children.Add(remember); panel.Children.Add(forget);
            panel.Children.Add(consent); panel.Children.Add(generate); panel.Children.Add(result);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Resumen del día", CloseButtonText = "Cerrar",
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, XamlRoot.Size.Height - 220) }
            };
            dialog.Closing += (_, _) => cancellation.Cancel();
            await dialog.ShowAsync();
        }
        catch { StatusText.Text = "No se pudo abrir el resumen. Cierra cualquier otro diálogo y vuelve a intentarlo."; }
        finally { cancellation.Cancel(); _dailySummaryOpen = false; }
    }
}
