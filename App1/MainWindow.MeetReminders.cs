using System;
using System.Linq;
using System.Threading;
using Anfeta.UI.Services.Search;
using Anfeta.UI.Services.Notion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Anfeta.UI;

public sealed partial class MainWindow
{
    private FrameworkElement CreateMeetReminderAction(IndexedFileReminder reminder)
    {
        var panel = new StackPanel { Spacing = 6 };
        var button = new Button { Content = "Abrir Meet", HorizontalAlignment = HorizontalAlignment.Stretch };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(button);
        panel.Children.Add(status);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Text = "Buscando el enlace en el mensaje…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            void Cancel(object sender, RoutedEventArgs args) => timeout.Cancel();
            panel.Unloaded += Cancel;
            try
            {
                var links = MeetLinkHelper.ExtractMeetLinks(reminder.Message, reminder.Title);
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Extracted {links.Count} Meet links: {string.Join(", ", links.Select(l => l.AbsoluteUri))}");
                if (links.Count == 0 && !string.IsNullOrWhiteSpace(reminder.PageId))
                {
                    var token = ApplicationData.Current.LocalSettings.Values["Notion.Token"] as string;
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("Configura la conexión de Notion para consultar el enlace.");
                    var thread = await new NotionMessageThreadService().GetThreadAsync(token, reminder.PageId, timeout.Token);
                    var threadText = string.Join("\n", thread.OrderByDescending(x => x.CreatedAt).Select(x => x.Text));
                    // Usar el mismo helper que ya aplica lógica de código y URL
                    links = MeetLinkHelper.ExtractMeetLinks(threadText, null);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (links.Count == 1)
                {
                    status.Text = await Windows.System.Launcher.LaunchUriAsync(links[0])
                        ? "Meet abierto: " + links[0].AbsoluteUri : "No se pudo abrir Meet. Intenta de nuevo.";
                }
                else if (links.Count > 1)
                {
                    status.Text = "El mensaje contiene varias salas. Elige la correcta:";
                    while (panel.Children.Count > 2) panel.Children.RemoveAt(2);
                    foreach (var link in links)
                        panel.Children.Add(new HyperlinkButton { Content = link.AbsoluteUri, NavigateUri = link });
                }
                else status.Text = "El mensaje no contiene una sala válida de Meet. Abre la conversación y pide que agreguen el enlace.";
            }
            catch (OperationCanceledException) { status.Text = "Consulta cancelada o sin respuesta. Puedes reintentar."; }
            catch (InvalidOperationException ex) { status.Text = ex.Message; }
            catch { status.Text = "No se pudo consultar el mensaje. Revisa la conexión de Notion y reintenta."; }
            finally { panel.Unloaded -= Cancel; button.IsEnabled = true; }
        };
        return panel;
    }
}
