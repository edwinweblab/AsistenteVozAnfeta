using System;
using System.IO;
using System.Linq;
using Anfeta.UI.Helpers;
using Anfeta.UI.Models.Weblab;

void AssertEqual<T>(T actual, T expected, string testName)
{
    if (!Equals(actual, expected))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {testName} -> Esperado: '{expected}', Obtenido: '{actual}'");
        Console.ResetColor();
        throw new InvalidOperationException($"Fallo en prueba '{testName}'");
    }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[PASS] {testName} -> '{actual}'");
    Console.ResetColor();
}

Console.WriteLine("=================================================");
Console.WriteLine("PRUEBAS DE CATEGORÍAS DROPBOX: FACTURACIÓN, INSPIRACIÓN Y LOGO");
Console.WriteLine("=================================================");

// 1. Verificación de existencia de las nuevas categorías en el selector de ProjectCategories
AssertEqual(ProjectSuffixHelper.ProjectCategories.Contains("facturacion"), true, "ProjectCategories contiene 'facturacion'");
AssertEqual(ProjectSuffixHelper.ProjectCategories.Contains("inspiracion"), true, "ProjectCategories contiene 'inspiracion'");
AssertEqual(ProjectSuffixHelper.ProjectCategories.Contains("logo"), true, "ProjectCategories contiene 'logo'");

// 2. Verificación de etiquetas con emojis para el selector
AssertEqual(ProjectSuffixHelper.CategoryDisplayLabels["facturacion"], "🧾 Facturación", "Label Facturación sin acento");
AssertEqual(ProjectSuffixHelper.CategoryDisplayLabels["facturación"], "🧾 Facturación", "Label Facturación con acento");
AssertEqual(ProjectSuffixHelper.CategoryDisplayLabels["inspiracion"], "💡 Inspiración", "Label Inspiración sin acento");
AssertEqual(ProjectSuffixHelper.CategoryDisplayLabels["inspiración"], "💡 Inspiración", "Label Inspiración con acento");
AssertEqual(ProjectSuffixHelper.CategoryDisplayLabels["logo"], "🏷️ Logo", "Label Logo");

// 3. Detección automática de FACTURACIÓN
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.facturacion prueba borrar.png", false, "weblab.mx"), "facturacion",
    "Detectar 'facturacion' con dominio weblab.mx");
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.facturación final.pdf", false, "weblab.mx"), "facturacion",
    "Detectar 'facturación' con tilde");
AssertEqual(ProjectSuffixHelper.GuessCategory("factura_proveedor_1024.pdf", false), "facturacion",
    "Detectar 'factura'");
AssertEqual(ProjectSuffixHelper.GuessCategory("cfdi_recibo_honorarios.xml", false), "facturacion",
    "Detectar 'cfdi' / '.xml'");

// 4. Detección automática de INSPIRACIÓN
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.inspiracion referencia clientes.png", false, "weblab.mx"), "inspiracion",
    "Detectar 'inspiracion' con dominio weblab.mx");
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.inspiración diseño.jpg", false, "weblab.mx"), "inspiracion",
    "Detectar 'inspiración' con tilde");
AssertEqual(ProjectSuffixHelper.GuessCategory("moodboard_ideas_sitio.png", false), "inspiracion",
    "Detectar 'moodboard'");
AssertEqual(ProjectSuffixHelper.GuessCategory("inspo_header_2026.png", false), "inspiracion",
    "Detectar 'inspo'");

// 5. Detección automática de LOGO
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.logo oficial vector.svg", false, "weblab.mx"), "logo",
    "Detectar 'logo' con dominio weblab.mx");
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.logotipo horizontal.png", false, "weblab.mx"), "logo",
    "Detectar 'logotipo'");
AssertEqual(ProjectSuffixHelper.GuessCategory("isotipo_empresa.png", false), "logo",
    "Detectar 'isotipo'");
AssertEqual(ProjectSuffixHelper.GuessCategory("branding_guia_estilo.pdf", false), "logo",
    "Detectar 'branding'");

// 6. Validación de prevención de falsos positivos con dominios tipo weblab.mx
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx captura pantalla.png", false, "weblab.mx"), "proyecto",
    "weblab.mx sin categoría debe ir a 'proyecto' (raíz) y NO a 'webs'");
AssertEqual(ProjectSuffixHelper.GuessCategory("weblab.mx.webs nueva seccion.png", false, "weblab.mx"), "webs",
    "weblab.mx con sufijo explícito .webs debe ir a 'webs'");

// 7. Parse de sufijos estándar en nombres de proyecto
var parsedFact = ProjectSuffixHelper.Parse("weblab.mx.facturacion");
AssertEqual(parsedFact.Domain, "weblab.mx", "Parse dominio de weblab.mx.facturacion");
AssertEqual(parsedFact.Suffix, "facturacion", "Parse sufijo facturacion");

var parsedInsp = ProjectSuffixHelper.Parse("weblab.mx.inspiracion");
AssertEqual(parsedInsp.Domain, "weblab.mx", "Parse dominio de weblab.mx.inspiracion");
AssertEqual(parsedInsp.Suffix, "inspiracion", "Parse sufijo inspiracion");

var parsedLogo = ProjectSuffixHelper.Parse("weblab.mx.logo");
AssertEqual(parsedLogo.Domain, "weblab.mx", "Parse dominio de weblab.mx.logo");
AssertEqual(parsedLogo.Suffix, "logo", "Parse sufijo logo");

// 8. Normalización de área codificada en SearchResultRow
AssertEqual(SearchResultRow.NormalizeEncodedArea("facturacion"), "FACTURACIÓN", "NormalizeEncodedArea facturacion");
AssertEqual(SearchResultRow.NormalizeEncodedArea("inspiracion"), "INSPIRACIÓN", "NormalizeEncodedArea inspiracion");
AssertEqual(SearchResultRow.NormalizeEncodedArea("logo"), "LOGO", "NormalizeEncodedArea logo");

// 9. Detección de área desde título
AssertEqual(SearchResultRow.DetectAreaFromTitleOrProject("weblab.mx facturacion 26-[10OCT]"), "FACTURACIÓN", "DetectArea facturacion");
AssertEqual(SearchResultRow.DetectAreaFromTitleOrProject("weblab.mx inspiracion referencias"), "INSPIRACIÓN", "DetectArea inspiracion");
AssertEqual(SearchResultRow.DetectAreaFromTitleOrProject("weblab.mx logo oficial"), "LOGO", "DetectArea logo");

// 10. Limpieza de dominio y extracción de prefijo
SearchResultRow.CleanProjectDomain("facturacion.weblab.mx", out var detectedFact);
AssertEqual(detectedFact, "FACTURACIÓN", "CleanProjectDomain detecta prefijo facturacion.");

SearchResultRow.CleanProjectDomain("inspiracion.weblab.mx", out var detectedInsp);
AssertEqual(detectedInsp, "INSPIRACIÓN", "CleanProjectDomain detecta prefijo inspiracion.");

SearchResultRow.CleanProjectDomain("logo.weblab.mx", out var detectedLogo);
AssertEqual(detectedLogo, "LOGO", "CleanProjectDomain detecta prefijo logo.");

// 11. Generación de rutas inteligentes en Dropbox
var tempDir = Path.Combine(Path.GetTempPath(), "AnfetaTest_" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(tempDir);
    var pathFact = ProjectSuffixHelper.ResolveAndEnsureSmartDropboxDestination(tempDir, "weblab.mx", false, "facturacion", out var dispFact);
    AssertEqual(dispFact.Replace('/', '\\'), @"weblab.mx.proyecto\weblab.mx.facturacion", "Ruta relativa Dropbox Facturación");

    var pathInsp = ProjectSuffixHelper.ResolveAndEnsureSmartDropboxDestination(tempDir, "weblab.mx", false, "inspiracion", out var dispInsp);
    AssertEqual(dispInsp.Replace('/', '\\'), @"weblab.mx.proyecto\weblab.mx.inspiracion", "Ruta relativa Dropbox Inspiración");

    var pathLogo = ProjectSuffixHelper.ResolveAndEnsureSmartDropboxDestination(tempDir, "weblab.mx", false, "logo", out var dispLogo);
    AssertEqual(dispLogo.Replace('/', '\\'), @"weblab.mx.proyecto\weblab.mx.logo", "Ruta relativa Dropbox Logo");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}

Console.WriteLine("=================================================");
Console.WriteLine("¡TODAS LAS PRUEBAS (11/11) PASARON EXITOSAMENTE!");
Console.WriteLine("=================================================");
