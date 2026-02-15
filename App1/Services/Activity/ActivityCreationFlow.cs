// Services/Activity/ActivityCreationFlow.cs
using System;
using System.Collections.Generic;
using System.Text;
using Anfeta.UI.Models;

namespace Anfeta.UI.Services.Activity
{
    /// <summary>
    /// Gestiona el flujo de creación de actividad (SIMPLIFICADO)
    /// Solo pregunta: Título, Prioridad, Fecha inicio (opcional)
    /// </summary>
    public sealed class ActivityCreationFlow
    {
        private readonly ActivityFieldExtractor _extractor;
        private readonly ActivityFieldValidator _validator;
        private readonly CorrectionCommandDetector _correctionDetector;

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
            _state = new ActivityCreationState();
            _missingFields = new List<string>();
        }

        /// <summary>
        /// Inicia el flujo con el comando inicial del usuario
        /// Entrada: initialCommand - "crear actividad enviar reporte..."
        /// Salida: Siguiente pregunta o confirmación
        /// </summary>
        public string Start(string initialCommand)
        {
            // Reset estado
            _state = new ActivityCreationState();
            _missingFields = new List<string>();

            // Extraer campos del comando inicial
            _state = _extractor.ExtractFields(initialCommand);

            // Determinar qué campos faltan (SOLO ESENCIALES)
            DetermineMissingFields();

            // Si no hay campos faltantes, ir a confirmación
            if (_missingFields.Count == 0)
            {
                _state.Phase = FlowPhase.Confirming;
                return GenerateConfirmation();
            }

            // Preguntar primer campo faltante
            _state.Phase = FlowPhase.Gathering;
            _state.CurrentStep = 0;
            return GetNextQuestion();
        }

        /// <summary>
        /// Procesa la respuesta del usuario
        /// Entrada: userResponse - respuesta del usuario
        /// Salida: (continuar, siguiente mensaje, datos para crear si está listo)
        /// </summary>
        public (bool shouldContinue, string message, ActivityCreationState? readyData) ProcessResponse(string userResponse)
        {
            if (string.IsNullOrWhiteSpace(userResponse))
                return (true, "No escuché tu respuesta. ¿Puedes repetir?", null);

            // Detectar cancelación (MEJORADO)
            if (_correctionDetector.IsCancellation(userResponse))
            {
                _state.Reset();
                return (false, "Creación de actividad cancelada.", null);
            }

            // Detectar reinicio
            if (_correctionDetector.IsRestart(userResponse))
            {
                _state.Reset();
                return (false, "Flujo reiniciado. Usa 'crear actividad' para empezar de nuevo.", null);
            }

            // Según la fase actual
            switch (_state.Phase)
            {
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

            if (!valid)
            {
                return (true, message, null);
            }

            // Campo válido, avanzar
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
        /// Determina qué campos faltan (SOLO ESENCIALES)
        /// </summary>
        private void DetermineMissingFields()
        {
            _missingFields.Clear();

            if (!_state.HasTitulo) _missingFields.Add("titulo");
            if (!_state.HasPrioridad) _missingFields.Add("prioridad");
            if (!_state.HasDueStart) _missingFields.Add("dueStart");
        }

        /// <summary>
        /// Obtiene la siguiente pregunta
        /// </summary>
        private string GetNextQuestion()
        {
            if (_state.CurrentStep >= _missingFields.Count)
                return GenerateConfirmation();

            var field = _missingFields[_state.CurrentStep];
            return GetQuestionForField(field);
        }

        /// <summary>
        /// Genera pregunta para un campo específico
        /// </summary>
        private string GetQuestionForField(string field)
        {
            return field switch
            {
                "titulo" => "¿Cuál es el título de la actividad?",
                "prioridad" => "¿Qué prioridad tendrá? Opciones: Alta, Media, Baja",
                "dueStart" => "¿Cuándo debe iniciar? Puedes decir fecha y hora, o 'sin fecha'",
                _ => "Campo desconocido"
            };
        }

        /// <summary>
        /// Genera pregunta de corrección
        /// </summary>
        private string GetCorrectionQuestion(string field)
        {
            return field switch
            {
                "titulo" => "¿Cuál es el nuevo título?",
                "prioridad" => "¿Cuál es la nueva prioridad? Opciones: Alta, Media, Baja",
                "dueStart" => "¿Cuál es la nueva fecha de inicio?",
                "dueEnd" => "¿Cuál es la nueva fecha de fin?",
                _ => $"¿Cuál es el nuevo valor para {field}?"
            };
        }

        /// <summary>
        /// Establece un campo con validación
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
                    // Permitir "sin fecha"
                    if (value.ToLowerInvariant().Contains("sin fecha") ||
                        value.ToLowerInvariant().Contains("no"))
                    {
                        _state.DueStart = null;
                        _state.DueEnd = null;
                        return (true, "Sin fecha establecida.");
                    }

                    var extractedState = _extractor.ExtractFields(value);
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