using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Anfeta.UI.Models.Weblab;

namespace Anfeta.UI.Services.Activity
{
    public sealed class ActivityEditFlow
    {
        private readonly ActivitiesCacheService _cache;
        private readonly ActivityEditFieldExtractor _fieldExtractor;
        private readonly ActivityEditFieldValidator _validator;

        private readonly ActivityEditState _state = new();

        public ActivityEditFlow(
            ActivitiesCacheService cache,
            ActivityEditFieldExtractor fieldExtractor,
            ActivityEditFieldValidator validator)
        {
            _cache = cache;
            _fieldExtractor = fieldExtractor;
            _validator = validator;
        }

        public bool IsActive => _state.Phase != EditFlowPhase.None;

        public void Reset() => _state.Reset();

        public string Start(string initialText)
        {
            _state.Reset();

            var searchText = ExtractActivitySearchText(initialText);
            if (string.IsNullOrWhiteSpace(searchText))
            {
                _state.Phase = EditFlowPhase.SearchingActivity;
                return "Dime el nombre de la actividad que quieres editar.";
            }

            _state.SearchText = searchText;

            var results = _cache.SearchByTitle(searchText);
            _state.SearchResults = results;

            if (results.Count == 0)
            {
                _state.Phase = EditFlowPhase.SearchingActivity;
                return $"No encontré actividades en cache con '{searchText}'. Dime otro nombre.";
            }

            if (results.Count == 1)
            {
                _state.SelectedActivity = results[0];

                var field = _fieldExtractor.TryExtractField(initialText);
                if (!string.IsNullOrWhiteSpace(field))
                {
                    _state.FieldToEdit = field;
                    _state.Phase = EditFlowPhase.AskingValue;
                    return $"Encontré '{results[0].Title}'. ¿Cuál será el nuevo valor para {GetFieldDisplayName(field)}?";
                }

                _state.Phase = EditFlowPhase.AskingField;
                return $"Encontré '{results[0].Title}'. ¿Qué campo quieres editar? Título, prioridad, estado, fecha inicio, fecha fin, anotaciones o pasos y links.";
            }

            _state.Phase = EditFlowPhase.SelectingActivity;
            return BuildSelectionMessage(results);
        }

        public (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessResponse(string userText)
        {
            var t = (userText ?? "").Trim().ToLowerInvariant();

            if (t == "cancelar" || t == "cancela" || t == "no" || t == "negativo")
            {
                _state.Reset();
                return (false, "Edición cancelada.", null, null);
            }
            switch (_state.Phase)
            {
                case EditFlowPhase.SearchingActivity:
                    return ProcessSearchingActivity(userText);

                case EditFlowPhase.SelectingActivity:
                    return ProcessSelectingActivity(userText);

                case EditFlowPhase.AskingField:
                    return ProcessAskingField(userText);

                case EditFlowPhase.AskingValue:
                    return ProcessAskingValue(userText);

                case EditFlowPhase.Confirming:
                    return ProcessConfirming(userText);

                default:
                    return (false, "No hay un flujo de edición activo.", null, null);
            }
        }
        private (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessSearchingActivity(string userText)
        {
            var results = _cache.SearchByTitle(userText);
            _state.SearchResults = results;

            if (results.Count == 0)
                return (true, $"No encontré actividades en cache con '{userText}'. Dime otro nombre.", null, null);

            if (results.Count == 1)
            {
                _state.SelectedActivity = results[0];
                _state.Phase = EditFlowPhase.AskingField;
                return (true, $"Encontré '{results[0].Title}'. ¿Qué campo quieres editar?", null, null);
            }

            _state.Phase = EditFlowPhase.SelectingActivity;
            return (true, BuildSelectionMessage(results), null, null);
        }

        private (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessSelectingActivity(string userText)
        {
            if (!int.TryParse(userText.Trim(), out var index))
                return (true, "Dime el número de la actividad que quieres editar.", null, null);

            if (index < 1 || index > _state.SearchResults.Count)
                return (true, "Número inválido. Intenta de nuevo.", null, null);

            _state.SelectedActivity = _state.SearchResults[index - 1];
            _state.Phase = EditFlowPhase.AskingField;

            return (true, $"Seleccionaste '{_state.SelectedActivity.Title}'. ¿Qué campo quieres editar?", null, null);
        }

        private (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessAskingField(string userText)
        {
            var field = _fieldExtractor.TryExtractField(userText);
            if (string.IsNullOrWhiteSpace(field))
            {
                return (true, "No entendí el campo. Puedes decir: título, prioridad, estado, fecha inicio, fecha fin, anotaciones o pasos y links.", null, null);
            }

            _state.FieldToEdit = field;
            _state.Phase = EditFlowPhase.AskingValue;

            return (true, $"¿Cuál será el nuevo valor para {GetFieldDisplayName(field)}?", null, null);
        }

        private (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessAskingValue(string userText)
        {
            if (string.IsNullOrWhiteSpace(_state.FieldToEdit))
                return (true, "Error interno: falta campo a editar.", null, null);

            var validation = _validator.Validate(_state.FieldToEdit, userText);
            if (!validation.Ok)
                return (true, validation.Message, null, null);

            _state.NewValueRaw = validation.Normalized;
            _state.Phase = EditFlowPhase.Confirming;

            var patch = BuildPatch();
            return (true, BuildConfirmationMessage(), _state.SelectedActivity, patch);
        }

        private (bool Continue, string Message, CachedActivityItem? Activity, UpdateActividadRequest? Patch) ProcessConfirming(string userText)
        {
            var t = userText.Trim().ToLowerInvariant();

            if (t == "confirmar" || t == "sí" || t == "si")
            {
                var patch = BuildPatch();
                var activity = _state.SelectedActivity;
                _state.Reset();
                return (false, "Confirmado.", activity, patch);
            }

            if (t.Contains("cancelar"))
            {
                _state.Reset();
                return (false, "Edición cancelada.", null, null);
            }

            return (true, "Responde 'confirmar' para guardar o 'cancelar' para salir.", null, null);
        }

        private UpdateActividadRequest BuildPatch()
        {
            var patch = new UpdateActividadRequest();

            switch (_state.FieldToEdit)
            {
                case "titulo":
                    patch.Titulo = _state.NewValueRaw;
                    break;
                case "prioridad":
                    patch.Prioridad = _state.NewValueRaw;
                    break;
                case "status":
                    patch.Status = _state.NewValueRaw;
                    break;
                case "dueStart":
                    patch.DueStart = _state.NewValueRaw;
                    break;
                case "dueEnd":
                    patch.DueEnd = _state.NewValueRaw;
                    break;
                case "anotaciones":
                    patch.Anotaciones = _state.NewValueRaw;
                    break;
                case "pasosYLinks":
                    patch.PasosYLinks = _state.NewValueRaw;
                    break;
            }

            return patch;
        }

        private string BuildSelectionMessage(List<CachedActivityItem> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Encontré varias actividades:");
            for (int i = 0; i < Math.Min(results.Count, 5); i++)
            {
                sb.AppendLine($"{i + 1}) {results[i].Title}");
            }
            sb.Append("Di el número de la que quieres editar.");
            return sb.ToString();
        }

        private string BuildConfirmationMessage()
        {
            var title = _state.SelectedActivity?.Title ?? "actividad";
            var field = GetFieldDisplayName(_state.FieldToEdit ?? "");
            var value = _state.NewValueRaw ?? "";

            return $"Voy a editar '{title}'. Campo: {field}. Nuevo valor: {value}. Responde 'confirmar' para guardar o 'cancelar' para salir.";
        }

        private string GetFieldDisplayName(string field)
        {
            return field switch
            {
                "titulo" => "título",
                "prioridad" => "prioridad",
                "status" => "estado",
                "dueStart" => "fecha inicio",
                "dueEnd" => "fecha fin",
                "anotaciones" => "anotaciones",
                "pasosYLinks" => "pasos y links",
                _ => field
            };
        }

        private string ExtractActivitySearchText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var t = text.Trim();

            t = t.Replace("editar actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("edita actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("editar", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("edita", "", StringComparison.OrdinalIgnoreCase);

            t = t.Replace("cambiar actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("cambia actividad", "", StringComparison.OrdinalIgnoreCase);

            t = t.Replace("modificar actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("modifica actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("modificar", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("modifica", "", StringComparison.OrdinalIgnoreCase);

            t = t.Replace("actualizar actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("actualiza actividad", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("actualizar", "", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("actualiza", "", StringComparison.OrdinalIgnoreCase);

            return t.Trim();
        }
    }
}