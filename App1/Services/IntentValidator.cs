using Anfeta.UI.Models;
using System;
using System.Linq;

namespace Anfeta.UI.Services
{
    /// <summary>Valida coherencia comando vs contexto</summary>
    public sealed class IntentValidator
    {
        private readonly ContextManager _contextManager;
        private readonly CapabilityRegistry _registry;

        public IntentValidator(ContextManager contextManager, CapabilityRegistry registry)
        {
            _contextManager = contextManager;
            _registry = registry;
        }

        /// <summary>Validar y enriquecer resultado de interpretación</summary>
        public ValidationResult Validate(InterpretationResult result, string originalSpeech)
        {
            var ctx = _contextManager.GetContext();

            // NORMALIZAR app_key usando sinónimos
            if (!string.IsNullOrWhiteSpace(result.AppKey))
            {
                result.AppKey = NormalizeAppKey(result.AppKey);
            }

            // Caso 1: CloseApp sin app activa
            if (result.Intent.Equals("CloseApp", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.CurrentApp == null)
                {
                    return ValidationResult.Rejected(
                        "No hay apps abiertas actualmente.",
                        suggestAlternative: "¿Quieres abrir alguna app?"
                    );
                }

                // INFERIR app a cerrar
                if (string.IsNullOrWhiteSpace(result.AppKey))
                {
                    result.AppKey = ctx.CurrentApp.AppKey;
                    return ValidationResult.InferredOk(
                        $"Infiero cerrar {ctx.CurrentApp.AppKey}",
                        result
                    );
                }
            }

            // Caso 2: OpenApp cuando app YA está abierta
            if (result.Intent.Equals("OpenApp", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.AppKey))
            {
                if (ctx.CurrentApp?.AppKey.Equals(result.AppKey, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return ValidationResult.Suggestion(
                        $"{_registry.GetApp(result.AppKey)?.FriendlyName ?? result.AppKey} ya está abierto.",
                        suggestAlternative: "¿Quieres traerlo al frente?"
                    );
                }
            }

            // Caso 3: WebSearch sin navegador
            if (result.Intent.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.CurrentApp?.Category != "navegador")
                {
                    return ValidationResult.Rejected(
                        "No hay navegador abierto.",
                        suggestAlternative: "¿Abro Chrome primero?"
                    );
                }
            }

            // Caso 4: App no registrada
            if (!string.IsNullOrWhiteSpace(result.AppKey) &&
                !_registry.IsRegistered(result.AppKey))
            {
                return ValidationResult.Rejected(
                    $"'{result.AppKey}' no está disponible.",
                    suggestAlternative: _registry.GetAllowedAppsMessage()
                );
            }

            // Caso 5: Comando muy ambiguo o sin sentido
            if (result.Intent.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                result.Confidence < 0.3)
            {
                return ValidationResult.Rejected(
                    "No entendí el comando.",
                    suggestAlternative: "¿Puedes repetir con otras palabras?"
                );
            }

            return ValidationResult.Ok();
        }

        /// <summary>Convierte sinónimos a app_key canónico</summary>
        private string NormalizeAppKey(string appKey)
        {
            // Primero intenta match directo
            if (_registry.IsRegistered(appKey))
                return appKey;

            // Busca en sinónimos
            var allApps = _registry.GetAllApps();
            foreach (var app in allApps)
            {
                if (app.Synonyms != null &&
                    app.Synonyms.Any(s => s.Equals(appKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return app.AppKey;
                }
            }

            // No encontrado, devuelve original
            return appKey;
        }
    }

    /// <summary>Resultado de validación</summary>
    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public bool WasInferred { get; set; }
        public string? Message { get; set; }
        public string? SuggestAlternative { get; set; }
        public InterpretationResult? EnrichedResult { get; set; }

        public static ValidationResult Ok() => new() { IsValid = true };

        public static ValidationResult InferredOk(string message, InterpretationResult enriched) =>
            new() { IsValid = true, WasInferred = true, Message = message, EnrichedResult = enriched };

        public static ValidationResult Rejected(string message, string? suggestAlternative = null) =>
            new() { IsValid = false, Message = message, SuggestAlternative = suggestAlternative };

        public static ValidationResult Suggestion(string message, string? suggestAlternative = null) =>
            new() { IsValid = false, Message = message, SuggestAlternative = suggestAlternative };
    }
}