using System;

namespace Anfeta.UI.Services
{
    public static class GroqConfig
    {
        // TEMPORAL: key en código (NO recomendado para prod)
        public static string ApiKey =>
            Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";

        // Modelos comunes en Groq (elige uno)
        // Ejemplos: "llama-3.1-8b-instant", "llama-3.1-70b-versatile", "mixtral-8x7b-32768"
        public const string ModelName = "llama-3.1-8b-instant";

        public const string BaseUrl = "https://api.groq.com/openai/v1/";
    }
}
