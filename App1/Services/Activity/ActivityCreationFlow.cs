// Services/Activity/ActivityCreationFlow.cs
using Anfeta.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Gestiona el flujo de creación de actividad con clarificación AM/PM y preguntas variadas
    /// </summary>
    public sealed class ActivityCreationFlow
    {
        private readonly ActivityFieldExtractor _extractor;
        private readonly ActivityFieldValidator _validator;
        private readonly CorrectionCommandDetector _correctionDetector;
        private readonly WeblabUsersClient _usersClient;
        private readonly Random _random;

        private ActivityCreationState _state;
        private List<string> _missingFields;

        public ActivityCreationFlow(
            ActivityFieldExtractor extractor,
            ActivityFieldValidator validator,
            CorrectionCommandDetector correctionDetector,
            WeblabUsersClient usersClient)
        {
            _extractor = extractor;
            _validator = validator;
            _correctionDetector = correctionDetector;
            _usersClient = usersClient;
            _random = new Random();
            _state = new ActivityCreationState();
            _missingFields = new List<string>();
        }

        /// <summary>
        /// Inicia el flujo extrayendo campos del comando inicial
        /// </summary>
        public string Start(string initialCommand)
        {
            _state = new ActivityCreationState();
            _missingFields = new List<string>();

            _state = _extractor.ExtractFields(initialCommand);

            if (_state.AmbiguousHour.HasValue && _state.AmbiguousBaseDate.HasValue)
            {
                _state.Phase = FlowPhase.ClarifyingTime;
                return GetTimeClarificationQuestion(_state.AmbiguousHour.Value);
            }

            DetermineMissingFields();

            if (_missingFields.Count > 0)
            {
                _state.Phase = FlowPhase.Gathering;
                _state.CurrentStep = 0;
                return GetNextQuestion();
            }

            // Todos los campos básicos completos
            // Verificar si necesita hora fin
            if (_state.DueStart.HasValue && !_state.DueEnd.HasValue)
            {
                _state.Phase = FlowPhase.AskingDueEnd;
                return GetDueEndQuestion();
            }

            // Todo completo, ir a assignee
            _state.Phase = FlowPhase.AskingAssignee;
            return GetAssigneeQuestion();
        }

        /// <summary>
        /// Procesa la respuesta del usuario según la fase actual
        /// </summary>
        public (bool shouldContinue, string message, ActivityCreationState? readyData) ProcessResponse(string userResponse)
        {
            if (string.IsNullOrWhiteSpace(userResponse))
                return (true, "No escuché tu respuesta. ¿Puedes repetir?", null);

            if (_correctionDetector.IsCancellation(userResponse))
            {
                _state.Reset();
                return (false, "Creación de actividad cancelada.", null);
            }

            if (_correctionDetector.IsRestart(userResponse))
            {
                _state.Reset();
                return (false, "Flujo reiniciado. Usa 'crear actividad' para empezar de nuevo.", null);
            }
            
            switch (_state.Phase)
            {
                case FlowPhase.ClarifyingTime:
                    return ProcessTimeClarificationResponse(userResponse);

                case FlowPhase.Gathering:
                    return ProcessGatheringResponse(userResponse);

                case FlowPhase.AskingDueEnd:  
                    return ProcessAskingDueEndResponse(userResponse);

                case FlowPhase.AskingAssignee:
                    return ProcessAskingAssigneeResponse(userResponse);

                case FlowPhase.SearchingAssignee:
                    return ProcessSearchingAssigneePhase();

                case FlowPhase.ConfirmingAssignee:
                    return ProcessConfirmingAssigneeResponse(userResponse);

                case FlowPhase.SelectingFromMultiple:
                    return ProcessSelectingFromMultipleResponse(userResponse);

                case FlowPhase.Confirming:
                    return ProcessConfirmationResponse(userResponse);

                case FlowPhase.Correcting:
                    return ProcessCorrectionResponse(userResponse);

                default:
                    return (false, "Error interno del flujo.", null);
            }

        }

        /// <summary>
        /// Procesa respuesta de clarificación AM/PM
        /// </summary>
        private (bool, string, ActivityCreationState?) ProcessTimeClarificationResponse(string response)
        {
            if (!_state.AmbiguousHour.HasValue || !_state.AmbiguousBaseDate.HasValue)
                return (false, "Error interno: falta hora ambigua.", null);

            var normalized = response.Trim().ToLowerInvariant()
                .Replace(" ", "")
                .Replace("despuésdemediodia", "pm")
                .Replace("despuésdemiodía", "pm")
                .Replace("despuesdemediodia", "pm")
                .Replace("despuésdelmediodía", "pm")
                .Replace("despuesdelmediodia", "pm")
                .Replace("antesdemediodia", "am")
                .Replace("antesdelmediodia", "am")
                .Replace("antesdemediodía", "am")
                .Replace("antesdelmediodía", "am")
                .Replace("delatarde", "pm")
                .Replace("delamañana", "am")
                .Replace("porlatarde", "pm")
                .Replace("porlamañana", "am");

            int finalHour;

            if (normalized.Contains("mañana") ||
                normalized.Contains("am") ||
                normalized.Contains("antesmediodia") ||
                normalized.Contains("madrugada"))
            {
                finalHour = _state.AmbiguousHour.Value;
                if (finalHour == 12) finalHour = 0;
            }
            else if (normalized.Contains("tarde") ||
                     normalized.Contains("pm") ||
                     normalized.Contains("despuesmediodia") ||
                     normalized.Contains("noche"))
            {
                finalHour = _state.AmbiguousHour.Value;
                if (finalHour != 12) finalHour += 12;
            }
            else if (normalized.Contains("mediodia") && _state.AmbiguousHour.Value == 12)
            {
                finalHour = 12;
            }
            else
            {
                return (true, GetTimeClarificationQuestion(_state.AmbiguousHour.Value), null);
            }

            _state.DueStart = _state.AmbiguousBaseDate.Value.AddHours(finalHour);
            _state.DueEnd = null;
            _state.AmbiguousHour = null;
            _state.AmbiguousBaseDate = null;

            var fechaResult = _validator.ValidateFecha(_state.DueStart.Value);
            if (!fechaResult.Valid)
            {
                _state.DueStart = null;
                _state.DueEnd = null;
                _state.Phase = FlowPhase.Gathering;
                return (true, fechaResult.Message ?? "Fecha inválida. Por favor dame otra fecha.", null);
            }

            DetermineMissingFields();

            if (_missingFields.Count > 0)
            {
                _state.Phase = FlowPhase.Gathering;
                _state.CurrentStep = 0;
                return (true, GetNextQuestion(), null);
            }

            // Verificar dueEnd
            if (_state.DueStart.HasValue && !_state.DueEnd.HasValue)
            {
                _state.Phase = FlowPhase.AskingDueEnd;
                return (true, GetDueEndQuestion(), null);
            }

            _state.Phase = FlowPhase.AskingAssignee;
            return (true, GetAssigneeQuestion(), null);
        }

        /// <summary>
        /// Procesa respuesta en fase de recopilación
        /// </summary>
        private (bool, string, ActivityCreationState?) ProcessGatheringResponse(string response)
        {
            if (_state.CurrentStep >= _missingFields.Count)
            {
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, GetAssigneeQuestion(), null);
            }

            var field = _missingFields[_state.CurrentStep];
            var (valid, message) = SetField(field, response);

            if (_state.AmbiguousHour.HasValue && _state.AmbiguousBaseDate.HasValue)
            {
                _state.Phase = FlowPhase.ClarifyingTime;
                return (true, message, null);
            }

            if (!valid)
            {
                return (true, message, null);
            }

            _state.CurrentStep++;

            if (_state.CurrentStep >= _missingFields.Count)
            {
                // Si tiene dueStart pero NO tiene dueEnd
                if (_state.DueStart.HasValue && !_state.DueEnd.HasValue)
                {
                    _state.Phase = FlowPhase.AskingDueEnd;
                    return (true, GetDueEndQuestion(), null);
                }

                // Si ya tiene todo, ir a assignee
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, GetAssigneeQuestion(), null);
            }

            return (true, GetNextQuestion(), null);
        }

        /// <summary>
        /// Procesa respuesta en fase de confirmación
        /// </summary>
        private (bool, string, ActivityCreationState?) ProcessConfirmationResponse(string response)
        {
            if (_correctionDetector.IsConfirmation(response))
            {
                return (false, "Creando actividad...", _state);
            }

            var (isCorrection, field) = _correctionDetector.Detect(response);
            if (isCorrection)
            {
                if (string.IsNullOrWhiteSpace(field))
                {
                    return (true, "¿Qué campo quieres corregir? (título, prioridad, fecha)", null);
                }

                _state.Phase = FlowPhase.Correcting;
                _state.FieldBeingCorrected = field;
                return (true, GetCorrectionQuestion(field), null);
            }

            return (true, "No entendí. Di 'confirmar' para crear, 'corregir [campo]' para editar o 'cancelar' para abortar.", null);
        }

        /// <summary>
        /// Procesa respuesta en fase de corrección
        /// </summary>
        private (bool, string, ActivityCreationState?) ProcessCorrectionResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(_state.FieldBeingCorrected))
                return (true, "Error interno de corrección.", null);

            var (valid, message) = SetField(_state.FieldBeingCorrected, response);

            if (!valid)
            {
                return (true, message, null);
            }

            _state.Phase = FlowPhase.Confirming;
            _state.FieldBeingCorrected = null;
            return (true, GenerateConfirmation(), null);
        }

        /// <summary>
        /// Determina campos faltantes
        /// </summary>
        private void DetermineMissingFields()
        {
            _missingFields.Clear();

            if (!_state.HasTitulo) _missingFields.Add("titulo");
            if (!_state.HasPrioridad) _missingFields.Add("prioridad");
            if (!_state.HasDueStart) _missingFields.Add("dueStart");
        }

        /// <summary>
        /// Obtiene siguiente pregunta
        /// </summary>
        private string GetNextQuestion()
        {
            if (_state.CurrentStep >= _missingFields.Count)
                return GenerateConfirmation();

            var field = _missingFields[_state.CurrentStep];
            return GetQuestionForField(field);
        }

        /// <summary>
        /// Genera pregunta variada para un campo
        /// </summary>
        private string GetQuestionForField(string field)
        {
            return field switch
            {
                "titulo" => GetRandomQuestion(new[]
                {
                    "¿Cuál es el título de la actividad?",
                    "¿Qué título tendrá?",
                    "¿Cómo se llamará la actividad?"
                }),

                "prioridad" => GetRandomQuestion(new[]
                {
                    "¿Qué prioridad tendrá? Opciones: Alta, Media, Baja",
                    "¿Cuál es la prioridad? Puedes decir: Alta, Media o Baja",
                    "¿Es urgente, normal o puede esperar? Dime: Alta, Media o Baja"
                }),

                "dueStart" => GetRandomQuestion(new[]
                {
                    "¿Cuándo debe iniciar? Puedes decir fecha y hora, o 'sin fecha'",
                    "¿Para cuándo es? Dame fecha y hora, o di 'sin fecha'",
                    "¿Cuándo empieza? Puedes decir 'hoy', 'mañana' con hora, o 'sin fecha'"
                }),

                _ => "Campo desconocido"
            };
        }

        /// <summary>
        /// Genera pregunta variada de clarificación AM/PM
        /// </summary>
        private string GetTimeClarificationQuestion(int hour)
        {
            return GetRandomQuestion(new[]
            {
                $"¿{hour} de la mañana o {hour} de la tarde?",
                $"¿Te refieres a {hour} AM o {hour} PM?",
                $"¿{hour} antes del mediodía o después del mediodía?"
            });
        }

        /// <summary>
        /// Genera pregunta variada de corrección
        /// </summary>
        private string GetCorrectionQuestion(string field)
        {
            return field switch
            {
                "titulo" => GetRandomQuestion(new[]
                {
                    "¿Cuál es el nuevo título?",
                    "¿Qué título quieres?",
                    "Dame el título correcto"
                }),

                "prioridad" => GetRandomQuestion(new[]
                {
                    "¿Cuál es la nueva prioridad? Opciones: Alta, Media, Baja",
                    "¿Qué prioridad será? Alta, Media o Baja",
                    "Dime la prioridad: Alta, Media o Baja"
                }),

                "dueStart" => GetRandomQuestion(new[]
                {
                    "¿Cuál es la nueva fecha de inicio?",
                    "¿Para cuándo será?",
                    "Dame la fecha y hora de inicio correcta"
                }),

                "dueEnd" => GetRandomQuestion(new[]
                {
                    "¿Cuál es la nueva fecha de fin?",
                    "¿Cuándo terminará?",
                    "Dame la hora de fin o duración correcta"
                }),

                "assignee" => GetRandomQuestion(new[]
                {
                    "¿Para quién será? Di 'para mí', nombre de persona, o 'omitir'",
                    "¿Quién será responsable?",
                    "¿A quién se la asignamos?"
                }),

                _ => $"¿Cuál es el nuevo valor para {field}?"
            };
        }

        /// <summary>
        /// Selecciona pregunta aleatoria de un array
        /// </summary>
        private string GetRandomQuestion(string[] options)
        {
            if (options == null || options.Length == 0)
                return "Error: sin opciones de pregunta";

            var index = _random.Next(options.Length);
            return options[index];
        }

        /// <summary>
        /// Establece campo con validación
        /// </summary>
        private (bool valid, string message) SetField(string field, string value)
        {
            switch (field)
            {
                case "titulo":
                    if (string.IsNullOrWhiteSpace(value))
                        return (false, "El título no puede estar vacío. ¿Cuál es el título?");

                    _state.Titulo = value.Trim();
                    return (true, "Título guardado.");

                case "prioridad":
                    var prioridadResult = _validator.ValidatePrioridad(value);
                    if (!prioridadResult.Valid)
                    {
                        if (!string.IsNullOrWhiteSpace(prioridadResult.Suggestion))
                            return (false, $"¿Te refieres a prioridad '{prioridadResult.Suggestion}'? (Sí/No)");
                        return (false, "Prioridad no válida. Opciones: Alta, Media, Baja");
                    }

                    _state.Prioridad = prioridadResult.Normalized;
                    return (true, $"Prioridad: {prioridadResult.Normalized}");

                case "dueStart":
                    if (value.ToLowerInvariant().Contains("sin fecha") ||
                        value.ToLowerInvariant().Contains("no"))
                    {
                        _state.DueStart = null;
                        _state.DueEnd = null;
                        return (true, "Sin fecha establecida.");
                    }

                    var extractedState = _extractor.ExtractFields(value);

                    if (extractedState.AmbiguousHour.HasValue && extractedState.AmbiguousBaseDate.HasValue)
                    {
                        _state.AmbiguousHour = extractedState.AmbiguousHour;
                        _state.AmbiguousBaseDate = extractedState.AmbiguousBaseDate;
                        return (false, GetTimeClarificationQuestion(extractedState.AmbiguousHour.Value));
                    }

                    if (extractedState.DueStart.HasValue)
                    {
                        var fechaResult = _validator.ValidateFecha(extractedState.DueStart.Value);
                        if (!fechaResult.Valid)
                            return (false, fechaResult.Message ?? "Fecha inválida");

                        _state.DueStart = extractedState.DueStart;

                        // Si estamos CORRIGIENDO y ya había un dueEnd, mantener la duración
                        if (_state.Phase == FlowPhase.Correcting && _state.DueEnd.HasValue)
                        {
                            var oldDuration = (_state.DueEnd.Value - _state.DueStart.Value).TotalHours;
                            _state.DueEnd = _state.DueStart.Value.AddHours(oldDuration);
                        }
                        else
                        {
                            // Primera vez: dejar null para que se pregunte después
                            _state.DueEnd = null;
                        }

                        var formatted = _state.DueStart.Value.ToString("dd/MM/yyyy HH:mm");
                        var warning = fechaResult.Message ?? "";
                        return (true, $"Fecha: {formatted}. {warning}");
                    }

                    return (false, "No pude entender la fecha. Intenta: 'mañana a las 5', 'hoy', 'el 15 de febrero a las 10'");

                case "dueEnd":
                    if (value.ToLowerInvariant().Contains("automático") ||
                        value.ToLowerInvariant().Contains("automatico"))
                    {
                        if (_state.DueStart.HasValue)
                        {
                            _state.DueEnd = _state.DueStart.Value.AddHours(1);
                            return (true, $"Fin: {_state.DueEnd.Value:dd/MM/yyyy HH:mm}");
                        }
                        return (false, "No hay fecha de inicio para calcular fin automático.");
                    }

                    var extractedEnd = _extractor.ExtractFields(value);
                    if (extractedEnd.DueStart.HasValue)
                    {
                        _state.DueEnd = extractedEnd.DueStart;
                        return (true, $"Fin: {_state.DueEnd.Value:dd/MM/yyyy HH:mm}");
                    }

                    return (false, "No pude entender la fecha de fin.");

                case "assignee":
                    // Redirigir a la lógica de assignee
                    _state.Phase = FlowPhase.AskingAssignee;
                    return (true, GetAssigneeQuestion());

                default:
                    return (false, "Campo desconocido.");
            }
        }

        /// <summary>
        /// Genera resumen de confirmación
        /// </summary>
        private string GenerateConfirmation()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Confirma creación de actividad:");
            sb.AppendLine($"• Título: {_state.Titulo ?? "Sin título"}");
            sb.AppendLine($"• Prioridad: {_state.Prioridad ?? "Media"}");

            if (_state.DueStart.HasValue)
            {
                sb.AppendLine($"• Inicio: {FormatDateTime(_state.DueStart.Value)}");
                if (_state.DueEnd.HasValue)
                    sb.AppendLine($"• Fin: {FormatDateTime(_state.DueEnd.Value)}");
            }
            else
            {
                sb.AppendLine("• Sin fecha");
            }

            if (_state.Assignees == null)
            {
                sb.AppendLine("• Asignado a: Ti mismo");
            }
            else if (_state.Assignees.Count == 0)
            {
                sb.AppendLine("• Sin asignar");
            }
            else
            {
                sb.Append("• Asignado a: ");
                sb.AppendLine(string.Join(", ", _state.Assignees.Select(a => a.Name)));
            }

            sb.AppendLine();
            sb.Append("Responde: 'Confirmar' para crear, 'Corregir [campo]' para editar o 'Cancelar' para abortar");

            return sb.ToString();
        }

        private string FormatDateTime(DateTimeOffset date)
        {
            var hour = date.Hour;
            var minute = date.Minute;
            string period;

            if (hour == 0)
            {
                hour = 12;
                period = "AM";
            }
            else if (hour < 12)
            {
                period = "AM";
            }
            else if (hour == 12)
            {
                period = "PM";
            }
            else
            {
                hour -= 12;
                period = "PM";
            }

            var minuteStr = minute > 0 ? $":{minute:00}" : "";
            return $"{date:dd/MM/yyyy} {hour}{minuteStr} {period}";
        }

        // Procesa respuesta de pregunta de assignee
        private (bool, string, ActivityCreationState?) ProcessAskingAssigneeResponse(string response)
        {
            var lower = response.Trim().ToLowerInvariant();

            // Opción 1: Para mí / yo
            if (lower.Contains("para mí") ||
                lower.Contains("para mi") ||
                lower.Contains("a mí") ||
                lower.Contains("a mi") ||
                lower.Contains("para ti") ||
                lower == "yo" ||
                lower == "mi" ||
                lower == "mí")
            {
                _state.Assignees = null;
                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            // Opción 2: Omitir / sin asignar
            if (lower.Contains("omitir") ||
                lower.Contains("sin asignar") ||
                lower.Contains("ninguno") ||
                lower.Contains("nadie") ||
                lower.Contains("continuar") ||
                lower == "no")
            {
                _state.Assignees = new List<AssigneeInfo>();
                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            // Opción 3: Para [nombre] - extraer el nombre
            if (lower.StartsWith("para "))
            {
                var name = response.Substring(5).Trim();

                // Limpiar palabras clave
                name = System.Text.RegularExpressions.Regex.Replace(
                    name,
                    @"\s+(prioridad|mañana|hoy|alta|media|baja).*",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                ).Trim();

                if (!string.IsNullOrWhiteSpace(name) && name.Length > 1)
                {
                    name = char.ToUpper(name[0]) + name.Substring(1);

                    _state.PendingAssigneeNames = new List<string> { name };
                    _state.CurrentAssigneeIndex = 0;
                    _state.Assignees = new List<AssigneeInfo>();

                    return StartSearchingAssignee(name);
                }
            }

            // Opción 4: Solo el nombre directo (sin "para")
            // Limpiar y buscar
            var cleanedResponse = response.Trim();
            cleanedResponse = System.Text.RegularExpressions.Regex.Replace(
                cleanedResponse,
                @"\s+(prioridad|mañana|hoy|alta|media|baja).*",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            ).Trim();

            if (!string.IsNullOrWhiteSpace(cleanedResponse) && cleanedResponse.Length > 1)
            {
                // Detectar "y" para múltiples
                if (cleanedResponse.ToLowerInvariant().Contains(" y "))
                {
                    var names = cleanedResponse.Split(new[] { " y ", ", ", "," }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(n => n.Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length > 1)
                        .Select(n => char.ToUpper(n[0]) + n.Substring(1))
                        .ToList();

                    if (names.Count > 0)
                    {
                        _state.PendingAssigneeNames = names;
                        _state.CurrentAssigneeIndex = 0;
                        _state.Assignees = new List<AssigneeInfo>();

                        return StartSearchingAssignee(names[0]);
                    }
                }
                else
                {
                    var name = char.ToUpper(cleanedResponse[0]) + cleanedResponse.Substring(1);

                    _state.PendingAssigneeNames = new List<string> { name };
                    _state.CurrentAssigneeIndex = 0;
                    _state.Assignees = new List<AssigneeInfo>();

                    return StartSearchingAssignee(name);
                }
            }

            return (true, GetAssigneeQuestion(), null);
        }

        private (bool, string, ActivityCreationState?) ProcessAskingDueEndResponse(string response)
        {
            var lower = response.Trim().ToLowerInvariant();

            // Opción 1: Default / una hora / omitir / continuar
            if (lower.Contains("una hora") ||
                lower.Contains("1 hora") ||
                lower == "default" ||
                lower == "omitir" ||
                lower == "continuar" ||
                lower == "automático" ||
                lower == "automatico")
            {
                if (_state.DueStart.HasValue)
                {
                    _state.DueEnd = _state.DueStart.Value.AddHours(1);
                    _state.Phase = FlowPhase.AskingAssignee;
                    return (true, GetAssigneeQuestion(), null);
                }
                return (false, "Error: no hay fecha de inicio.", null);
            }

            // Opción 2: Sin fecha fin / no / ninguna
            if (lower.Contains("sin fecha") ||
                lower.Contains("no tiene") ||
                lower == "no" ||
                lower == "ninguna")
            {
                _state.DueEnd = null;
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, GetAssigneeQuestion(), null);
            }

            // Opción 3: Duración en horas "2 horas", "tres horas"
            var duracionMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+|una|dos|tres|cuatro|cinco)\s+horas?");
            if (duracionMatch.Success)
            {
                var num = duracionMatch.Groups[1].Value;
                var horas = num switch
                {
                    "una" => 1,
                    "dos" => 2,
                    "tres" => 3,
                    "cuatro" => 4,
                    "cinco" => 5,
                    _ => int.TryParse(num, out var n) ? n : (int?)null
                };

                if (horas.HasValue && _state.DueStart.HasValue)
                {
                    _state.DueEnd = _state.DueStart.Value.AddHours(horas.Value);
                    _state.Phase = FlowPhase.AskingAssignee;
                    return (true, GetAssigneeQuestion(), null);
                }
            }

            // Opción 4: Hora específica "hasta las 5", "termina a las 6"
            var horaMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(?:hasta|termina|a)\s+las?\s+(\d{1,2})");
            if (horaMatch.Success && int.TryParse(horaMatch.Groups[1].Value, out var hora))
            {
                if (_state.DueStart.HasValue)
                {
                    var baseDate = _state.DueStart.Value.Date;

                    // Detectar AM/PM
                    bool isPM = lower.Contains("tarde") || lower.Contains("pm") || lower.Contains("noche");

                    if (hora >= 1 && hora <= 12)
                    {
                        if (!isPM && !lower.Contains("am") && !lower.Contains("mañana"))
                        {
                            // Hora ambigua, asumir PM si es menor que hora de inicio
                            var horaInicio = _state.DueStart.Value.Hour;
                            if (hora <= horaInicio && hora != 12)
                                hora += 12;
                        }
                        else if (isPM && hora != 12)
                        {
                            hora += 12;
                        }
                        else if (hora == 12 && lower.Contains("am"))
                        {
                            hora = 0;
                        }
                    }

                    _state.DueEnd = baseDate.AddHours(hora);

                    // Si fin es antes que inicio, asumir día siguiente
                    if (_state.DueEnd.Value < _state.DueStart.Value)
                        _state.DueEnd = _state.DueEnd.Value.AddDays(1);

                    _state.Phase = FlowPhase.AskingAssignee;
                    return (true, GetAssigneeQuestion(), null);
                }
            }

            // No entendió, volver a preguntar
            return (true, GetDueEndQuestion(), null);
        }

        // Inicia búsqueda de assignee
        private (bool, string, ActivityCreationState?) StartSearchingAssignee(string name)
        {
            _state.PendingAssigneeName = name;
            _state.Phase = FlowPhase.SearchingAssignee;

            System.Diagnostics.Debug.WriteLine($"[FLOW] Iniciando búsqueda de '{name}'");

            // Ejecutar búsqueda en Task.Run para evitar deadlock
            _state.PendingSearchTask = Task.Run(async () => await _usersClient.SearchUsersAsync(name));

            return ProcessSearchingAssigneePhase();
        }

        // Ejecuta búsqueda y procesa resultados
        private (bool, string, ActivityCreationState?) ProcessSearchingAssigneePhase()
        {
            if (_state.PendingSearchTask == null)
            {
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, "Error en búsqueda. ¿A quién quieres asignar?", null);
            }

            System.Diagnostics.Debug.WriteLine("[FLOW] Obteniendo resultado de búsqueda...");

            UserSearchResponse results;
            try
            {
                System.Diagnostics.Debug.WriteLine("[FLOW] Antes de GetResult");

                // Usar GetAwaiter().GetResult()
                results = _state.PendingSearchTask.GetAwaiter().GetResult();
                _state.PendingSearchTask = null;

                System.Diagnostics.Debug.WriteLine($"[FLOW] Después de GetResult. Items: {results?.Items?.Count ?? -1}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FLOW] Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[FLOW] Stack: {ex.StackTrace}");
                _state.PendingSearchTask = null;
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, $"Error en búsqueda: {ex.Message}. ¿Intentar de nuevo?", null);
            }

            if (!results.Success)
            {
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, $"Error: {results.Error}. ¿Quieres intentar con otro nombre?", null);
            }

            if (results.Items.Count == 0)
            {
                _state.Phase = FlowPhase.AskingAssignee;
                return (true, $"No encontré a {_state.PendingAssigneeName}. ¿Otro nombre o email?", null);
            }

            if (results.Items.Count == 1)
            {
                _state.PendingSearchResults = results.Items;
                _state.Phase = FlowPhase.ConfirmingAssignee;
                var user = results.Items[0];
                return (true, $"Encontré a {user.FirstName} {user.LastName} ({user.Email}). ¿Es correcto?", null);
            }

            _state.PendingSearchResults = results.Items;
            _state.Phase = FlowPhase.SelectingFromMultiple;
            return (true, GenerateMultipleOptionsMessage(results.Items), null);
        }

        // Procesa confirmación de assignee (1 resultado)
        private (bool, string, ActivityCreationState?) ProcessConfirmingAssigneeResponse(string response)
        {
            var lower = response.Trim().ToLowerInvariant();

            if (lower.Contains("sí") || lower.Contains("si") || lower.Contains("correcto") || lower.Contains("confirmar"))
            {
                var user = _state.PendingSearchResults![0];
                _state.Assignees!.Add(new AssigneeInfo
                {
                    Name = $"{user.FirstName} {user.LastName}",
                    Email = user.Email,
                    CollaboratorId = user.CollaboratorId
                });

                _state.PendingSearchResults = null;

                if (_state.CurrentAssigneeIndex < _state.PendingAssigneeNames!.Count - 1)
                {
                    _state.CurrentAssigneeIndex++;
                    var nextName = _state.PendingAssigneeNames[_state.CurrentAssigneeIndex];
                    return StartSearchingAssignee(nextName);
                }

                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            if (lower.Contains("no") || lower.Contains("cancelar"))
            {
                _state.PendingSearchResults = null;
                return (true, "¿Cómo se llama la persona? Dame otro nombre o email.", null);
            }

            return (true, "No entendí. ¿Es correcto? Di 'sí' o 'no'.", null);
        }

        // Procesa selección entre múltiples resultados
        private (bool, string, ActivityCreationState?) ProcessSelectingFromMultipleResponse(string response)
        {
            var lower = response.Trim();

            if (int.TryParse(lower, out var selection) &&
                selection >= 1 &&
                selection <= _state.PendingSearchResults!.Count)
            {
                var user = _state.PendingSearchResults[selection - 1];
                _state.Assignees!.Add(new AssigneeInfo
                {
                    Name = $"{user.FirstName} {user.LastName}",
                    Email = user.Email,
                    CollaboratorId = user.CollaboratorId
                });

                _state.PendingSearchResults = null;

                if (_state.CurrentAssigneeIndex < _state.PendingAssigneeNames!.Count - 1)
                {
                    _state.CurrentAssigneeIndex++;
                    var nextName = _state.PendingAssigneeNames[_state.CurrentAssigneeIndex];
                    return StartSearchingAssignee(nextName);
                }

                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            return (true, "Opción no válida. Di el número (1, 2, 3, etc.)", null);
        }

        // Genera pregunta de asignación
        private string GetAssigneeQuestion()
        {
            return GetRandomQuestion(new[]
            {
                "¿Esta actividad es para ti o quieres asignársela a otra persona? Di: 'para mí', nombre de persona, o 'omitir'",
                "¿Quién será responsable? Puedes decir: 'para mí', el nombre de alguien, o 'omitir'",
                "¿Es para ti o para otra persona? Responde: 'para mí', nombre, o 'omitir'"
            });
        }

        private string GetDueEndQuestion()
        {
            return GetRandomQuestion(new[]
            {
                "¿Hora de fin? Di la hora, 'una hora' para default, u 'omitir'",
                "¿Hasta qué hora? Puedes decir hora o 'continuar'",
                "¿Cuánto durará? Di las horas o 'omitir'"
            });
        }

        public ActivityCreationState GetCurrentState() => _state;
        public bool IsActive() => _state.Phase != FlowPhase.Gathering || _state.HasTitulo;
        
        
        // Genera mensaje con opciones múltiples
        private string GenerateMultipleOptionsMessage(List<UserSearchItem> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Encontré {results.Count} personas:");

            for (int i = 0; i < Math.Min(results.Count, 5); i++)
            {
                var u = results[i];
                sb.AppendLine($"{i + 1}) {u.FirstName} {u.LastName} - {u.Email}");
            }

            sb.AppendLine("¿A cuál te refieres? Di el número.");
            return sb.ToString();
        }


    }
}