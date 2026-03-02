using Anfeta.UI.Services.Interpretation;
using System;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Groq
{
    public class GroqRuntimeService
    {
        private readonly ApiKeyService _keys;
        private readonly ICommandInterpretationService _interpreter;
        private readonly InterpretationCache _cache; // si existe en tu proyecto

        public GroqRuntimeService(
            ApiKeyService keys,
            ICommandInterpretationService interpreter,
            InterpretationCache cache)
        {
            _keys = keys;
            _interpreter = interpreter;
            _cache = cache;
        }

        public async Task<(bool ok, string? error)> WarmupAsync()
        {
            try
            {
                var key = await _keys.GetActiveGroqKeyAsync();
                if (string.IsNullOrWhiteSpace(key))
                    return (false, "No hay API key activa. Activa una para usar Groq.");

                // Opcional: limpiar cualquier cache que te deje el estado en "no disponible"
                _cache?.Clear(); // crea Clear() si aún no existe

                await _interpreter.InterpretRawAsync("ping");
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
