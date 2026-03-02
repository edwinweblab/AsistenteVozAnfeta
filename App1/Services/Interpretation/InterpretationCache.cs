using Anfeta.UI.Models.Interpretation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anfeta.UI.Services.Interpretation
{
    /// <summary>Caché en memoria para comandos interpretados (reduce latencia en comandos repetidos)</summary>
    public sealed class InterpretationCache
    {
        private readonly Dictionary<string, CachedInterpretation> _cache = new();
        private const int MaxCacheSize = 50;
        private const int CacheExpiryMinutes = 30;

        /// <summary>Intentar obtener resultado cacheado</summary>
        public bool TryGet(string speech, out InterpretationResult? result)
        {
            var key = NormalizeKey(speech);

            if (_cache.TryGetValue(key, out var cached))
            {
                var elapsed = (DateTime.UtcNow - cached.CachedAt).TotalMinutes;

                if (elapsed < CacheExpiryMinutes)
                {
                    System.Diagnostics.Debug.WriteLine($"[CACHE] HIT: {key} (age: {elapsed:F1}min)");
                    result = cached.Result;
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"[CACHE] EXPIRED: {key} (age: {elapsed:F1}min)");
                _cache.Remove(key);
            }

            result = null;
            return false;
        }

        /// <summary>Guardar resultado en caché</summary>
        public void Set(string speech, InterpretationResult result)
        {
            var key = NormalizeKey(speech);

            if (_cache.Count >= MaxCacheSize)
            {
                var oldest = _cache.OrderBy(kvp => kvp.Value.CachedAt).First();
                _cache.Remove(oldest.Key);
                System.Diagnostics.Debug.WriteLine($"[CACHE] EVICT: {oldest.Key}");
            }

            _cache[key] = new CachedInterpretation
            {
                Result = result,
                CachedAt = DateTime.UtcNow
            };

            System.Diagnostics.Debug.WriteLine($"[CACHE] SET: {key}");
        }

        /// <summary>Limpiar caché completo</summary>
        public void Clear()
        {
            _cache.Clear();
            System.Diagnostics.Debug.WriteLine("[CACHE] CLEAR");
        }

        /// <summary>Normalizar clave (lowercase, trim)</summary>
        private static string NormalizeKey(string speech)
        {
            return speech.Trim().ToLowerInvariant();
        }

        private sealed class CachedInterpretation
        {
            public InterpretationResult Result { get; set; } = null!;
            public DateTime CachedAt { get; set; }
        }
    }
}