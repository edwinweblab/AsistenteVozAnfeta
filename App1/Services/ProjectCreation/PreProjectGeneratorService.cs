using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.ProjectCreation
{
    public sealed record PreProjectStep(
        string Title,
        string Category,
        string Description,
        bool IsCompleted = false);

    public sealed record GeneratedPreProjectPlan(
        string Domain,
        string Suffix,
        string FullName,
        IReadOnlyList<PreProjectStep> Steps,
        IReadOnlyList<string> RequiredDropboxFolders,
        string InitialWhatsAppMessage);

    public static class PreProjectGeneratorService
    {
        public static GeneratedPreProjectPlan GeneratePlan(string domain, string? suffix = null)
        {
            var cleanDomain = (domain ?? string.Empty).Trim().ToLowerInvariant();
            if (!cleanDomain.Contains('.'))
            {
                cleanDomain = $"{cleanDomain}.com";
            }

            var cleanSuffix = Anfeta.UI.Helpers.ProjectSuffixHelper.NormalizeSuffix(suffix);
            if (string.IsNullOrEmpty(cleanSuffix))
            {
                cleanSuffix = Anfeta.UI.Helpers.ProjectSuffixHelper.SUFFIX_PREPROYECTO;
            }

            var fullName = $"{cleanDomain}.{cleanSuffix}";

            var steps = new List<PreProjectStep>
            {
                new(
                    Title: "1. Crear estructura de carpetas en Dropbox DRX",
                    Category: "Almacenamiento",
                    Description: $"Crear carpeta DRX/{cleanDomain}.Carpeta y subdirectorios de trabajo (.webs, .ads, .cotizacion, .auditoria, etc.)"),
                new(
                    Title: "2. Creación del grupo de WhatsApp con cliente",
                    Category: "Comunicación",
                    Description: $"Crear grupo de WhatsApp para {cleanDomain} e invitar a los integrantes responsables (John, Isaias, Carla, etc.) y al cliente."),
                new(
                    Title: "3. Página base de Notion para Pre-Proyecto",
                    Category: "Notion",
                    Description: $"Registrar la página {cleanDomain}.preproyecto con checklist inicial, responsables asignados y fecha de arranque."),
                new(
                    Title: "4. Auditoría y Benchmark de Inspiración",
                    Category: "Inspiración / Auditoría",
                    Description: $"Recopilar referencias de la competencia y capturas de inspiración en {cleanDomain}.auditoria y referencias en inspiracion.com.webs."),
                new(
                    Title: "5. Cotización y Alcance formal",
                    Category: "Finanzas",
                    Description: $"Definir alcance, paquetes y presupuesto en {cleanDomain}.cotizacion antes de liberar a producción."),
                new(
                    Title: "6. Onboarding inicial y reunión con el cliente",
                    Category: "Atención Cliente",
                    Description: "Enviar mensaje de bienvenida y agendar llamada de kick-off para confirmación de requerimientos clave.")
            };

            var folders = new List<string>
            {
                $"{cleanDomain}.Carpeta",
                $"{cleanDomain}.webs",
                $"{cleanDomain}.ads",
                $"{cleanDomain}.auditoria",
                $"{cleanDomain}.cotizacion"
            };

            var whatsAppMsg =
                $"¡Hola! Bienvenidos al grupo de trabajo de *{cleanDomain}* 🚀.\n" +
                $"Estaremos coordinando aquí todo el desarrollo, avances y entregables de su proyecto web y estrategias digitales con el equipo de Weblab.\n\n" +
                $"Cualquier duda o solicitud estamos a sus órdenes.";

            return new GeneratedPreProjectPlan(
                Domain: cleanDomain,
                Suffix: cleanSuffix,
                FullName: fullName,
                Steps: steps,
                RequiredDropboxFolders: folders,
                InitialWhatsAppMessage: whatsAppMsg);
        }
    }
}
