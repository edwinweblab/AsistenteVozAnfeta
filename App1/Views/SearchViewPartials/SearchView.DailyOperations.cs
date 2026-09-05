using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace Anfeta.UI.Views
{
    public sealed partial class SearchView
    {
        internal static bool TryGetProjectWebsite(string? domain, out Uri? uri)
        {
            uri = null;
            var value = (domain ?? "").Trim();
            if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('\\')) return false;
            if (!value.Contains("://")) value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
                (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
                candidate.UserInfo.Length != 0 || candidate.HostNameType != UriHostNameType.Dns ||
                !candidate.Host.Contains('.') || Uri.CheckHostName(candidate.Host) != UriHostNameType.Dns)
                return false;
            uri = candidate;
            return true;
        }

        private async void ActiveTodayProject_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!TryGetProjectWebsite((sender as FrameworkElement)?.Tag?.ToString(), out var uri))
            {
                StatusText.Text = "Estado: El proyecto no tiene un dominio web válido.";
                return;
            }
            try
            {
                StatusText.Text = await Launcher.LaunchUriAsync(uri!)
                    ? $"Estado: Sitio abierto · {uri!.Host}"
                    : "Estado: Windows no pudo abrir el navegador.";
            }
            catch (Exception)
            {
                StatusText.Text = "Estado: No se pudo abrir el sitio web. Revisa tu navegador predeterminado.";
            }
        }

        private static IEnumerable<string> OrderRecentCalendarPeople(IEnumerable<string> people)
        {
            var recent = LoadNotionUploadRecentTags()
                .Select(tag => tag.EndsWith("00", StringComparison.OrdinalIgnoreCase) ? tag[..^2] : tag)
                .Select(GetNotionPersonDisplayName).ToList();
            return people.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(person =>
                {
                    var rank = recent.FindIndex(value => string.Equals(value, person, StringComparison.OrdinalIgnoreCase));
                    return rank < 0 ? int.MaxValue : rank;
                });
        }

        private async void InviteMeet_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (BtnMeetQuickLinks.Flyout is not MenuFlyout menu) return;
                var rooms = new ComboBox { Header = "Sala de Meet", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                    if (TryGetMeetUri(item.Tag?.ToString(), out var uri))
                        rooms.Items.Add(new ComboBoxItem { Content = item.Text, Tag = uri!.AbsoluteUri });
                if (rooms.Items.Count == 0) { StatusText.Text = "Estado: No hay salas de Meet disponibles."; return; }
                rooms.SelectedIndex = 0;
                var picker = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Invitar a Meet", Content = rooms,
                    PrimaryButtonText = "Elegir destinatario", CloseButtonText = "Cancelar"
                };
                if (await picker.ShowAsync() != ContentDialogResult.Primary || rooms.SelectedItem is not ComboBoxItem room) return;
                await ShowNewMessageDialogAsync(new NewMessageComposerContext
                {
                    ActivityTitle = "Conéctate a este Meet",
                    SuggestedBody = $"Conéctate a este Meet: {room.Content}\n{room.Tag}",
                    SingleRecipientOnly = true,
                    SuggestedAt = DateTimeOffset.Now
                });
            }
            catch (Exception)
            {
                StatusText.Text = "Estado: No se pudo abrir la invitación a Meet. Inténtalo nuevamente.";
            }
        }

        private static bool TryGetMeetUri(string? value, out Uri? uri)
        {
            uri = null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "meet.google.com" ||
                candidate.UserInfo.Length != 0 || candidate.AbsolutePath.Length < 2) return false;
            uri = candidate;
            return true;
        }

        public async Task<bool> TryExecuteDailyActionAsync(string phrase)
        {
            var command = NormalizeCalendarSearchText(phrase).Trim().TrimEnd('.', '?', '!');
            if (command == "proyectos activos hoy" || command == "consultar proyectos activos hoy")
            {
                await ShowCalendarAsync(DateTime.Today);
                ActiveTodayProjectsCard.StartBringIntoView();
                return true;
            }
            if (command.StartsWith("actividades de ", StringComparison.Ordinal))
            {
                var requested = command[15..].Trim();
                var person = ActiveCalendarPeople.FirstOrDefault(p => NormalizeCalendarSearchText(p) == requested);
                if (person == null) return false;
                await ShowCalendarAsync(DateTime.Today);
                ShowCalendarPersonPreview(person);
                return true;
            }
            if (command.StartsWith("abrir proyecto ", StringComparison.Ordinal))
            {
                var domain = phrase.Trim()[15..].Trim().TrimEnd('.', '?', '!');
                if (!TryGetProjectWebsite(domain, out _)) return false;
                ActiveTodayProject_Click(new Button { Tag = domain }, new RoutedEventArgs());
                return true;
            }
            var create = System.Text.RegularExpressions.Regex.Match(command,
                @"^crear actividad(?: para (?<person>.+?))?(?: (?<day>hoy|manana))?$");
            if (create.Success)
            {
                var requestedPerson = create.Groups["person"].Value;
                var person = requestedPerson.Length == 0 ? GetNotionPersonDisplayName(GetCurrentMessagesUserTag()) :
                    ActiveCalendarPeople.FirstOrDefault(value => NormalizeCalendarSearchText(value) == requestedPerson);
                if (person == null) return false;
                if (!ActiveCalendarPeople.Contains(person)) person = "Sin asignar";
                await ShowCalendarCreateFromEmptySlotDialogAsync(new CalendarQuickCreationSeed(
                    person, DateTime.Today.AddDays(create.Groups["day"].Value == "manana" ? 1 : 0).AddHours(9), 60));
                return true;
            }
            var roomNumber = command switch { "abrir meet uno" or "abrir meet 1" => 0,
                "abrir meet dos" or "abrir meet 2" or "abrir meet omp" => 1,
                "abrir meet tres" or "abrir meet 3" => 2, _ => -1 };
            if (roomNumber >= 0 && BtnMeetQuickLinks.Flyout is MenuFlyout menu)
            {
                var room = menu.Items.OfType<MenuFlyoutItem>().Where(item => TryGetMeetUri(item.Tag?.ToString(), out _)).ElementAtOrDefault(roomNumber);
                if (room == null) return false;
                OpenMeetQuickLink_Click(room, new RoutedEventArgs());
                return true;
            }
            return false;
        }
    }
}
