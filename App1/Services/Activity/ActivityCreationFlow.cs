// Services/Activity/ActivityCreationFlow.cs
using System;
using System.Collections.Generic;
using System.Text;
using Anfeta.UI.Models;

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
        private readonly Random _random;

        private ActivityCreationState _state;
        private List<string> _missingFields;

        public ActivityCreationFlow(
            ActivityFieldExtractor extractor,
            ActivityFieldValidator validator,
            CorrectionCommandDetector correctionDetector)
        {
            _extractor = extractor;
            _validator = validator;
            _correctionDetector = correctionDetector;
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

            // Verificar si hay hora ambigua
            if (_state.AmbiguousHour.HasValue && _state.AmbiguousBaseDate.HasValue)
            {
                _state.Phase = FlowPhase.ClarifyingTime;
                return GetTimeClarificationQuestion(_state.AmbiguousHour.Value);
            }

            DetermineMissingFields();

            if (_missingFields.Count == 0)
            {
                _state.Phase = FlowPhase.Confirming;
                return GenerateConfirmation();
            }

            _state.Phase = FlowPhase.Gathering;
            _state.CurrentStep = 0;
            return GetNextQuestion();
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

            // ✅ NORMALIZAR: unificar todas las variaciones
            var normalized = response.Trim().ToLowerInvariant()
                .Replace(" ", "")
                .Replace("despuésdemediodia", "pm")
                .Replace("despuesdemiodía", "pm")
                .Replace("despuesdemediodia", "pm")
                .Replace("despuésdelmediodía", "pm")
                .Replace("despuesdelmediodia", "pm")
                .Replace("antesdemediodia", "am")
                .Replace("antesdelmediodia", "am")
                .Replace("antesdemediodía", "am")
                .Replace("antesdelmediodía", "am");

            int finalHour;

            // Detectar AM
            if (normalized.Contains("mañana") || normalized.Contains("am"))
            {
                finalHour = _state.AmbiguousHour.Value;
                if (finalHour == 12) finalHour = 0;
            }
            // Detectar PM
            else if (normalized.Contains("tarde") || normalized.Contains("pm"))
            {
                finalHour = _state.AmbiguousHour.Value;
                if (finalHour != 12) finalHour += 12;
            }
            else
            {
                return (true, GetTimeClarificationQuestion(_state.AmbiguousHour.Value), null);
            }

            _state.DueStart = _state.AmbiguousBaseDate.Value.AddHours(finalHour);
            _state.DueEnd = _state.DueStart.Value.AddHours(1);

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

            if (_missingFields.Count == 0)
            {
                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            _state.Phase = FlowPhase.Gathering;
            _state.CurrentStep = 0;
            return (true, GetNextQuestion(), null);
        }

        /// <summary>
        /// Procesa respuesta en fase de recopilación
        /// </summary>
        private (bool, string, ActivityCreationState?) ProcessGatheringResponse(string response)
        {
            if (_state.CurrentStep >= _missingFields.Count)
            {
                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
            }

            var field = _missingFields[_state.CurrentStep];
            var (valid, message) = SetField(field, response);

            // Detectar si activó clarificación de hora
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
                _state.Phase = FlowPhase.Confirming;
                return (true, GenerateConfirmation(), null);
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
                    "Dame la fecha y hora correcta"
                }),

                "dueEnd" => GetRandomQuestion(new[]
                {
                    "¿Cuál es la nueva fecha de fin?",
                    "¿Cuándo terminará?",
                    "Dame la fecha de finalización"
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
                        _state.DueEnd = extractedState.DueEnd ?? extractedState.DueStart.Value.AddHours(1);

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
                sb.AppendLine($"• Inicio: {_state.DueStart.Value:dd/MM/yyyy HH:mm}");
                if (_state.DueEnd.HasValue)
                    sb.AppendLine($"• Fin: {_state.DueEnd.Value:dd/MM/yyyy HH:mm}");
            }
            else
            {
                sb.AppendLine("• Sin fecha");
            }

            sb.AppendLine();
            sb.Append("Responde: 'Confirmar' para crear, 'Corregir [campo]' para editar o 'Cancelar' para abortar");

            return sb.ToString();
        }

        public ActivityCreationState GetCurrentState() => _state;
        public bool IsActive() => _state.Phase != FlowPhase.Gathering || _state.HasTitulo;
    }
}