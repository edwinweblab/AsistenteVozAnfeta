using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Reports;

public static class DailyReportGenerator
{
    public static async Task<string> GenerateHtmlAsync(string jsonPayloadPath, string outputHtmlPath, CancellationToken cancellationToken = default)
    {
        var jsonContent = await File.ReadAllTextAsync(jsonPayloadPath, cancellationToken);
        var htmlContent = BuildReportHtml(jsonContent);
        await File.WriteAllTextAsync(outputHtmlPath, htmlContent, cancellationToken);
        if (!File.Exists(outputHtmlPath))
            throw new InvalidOperationException("No se pudo generar el archivo HTML del reporte.");
        return outputHtmlPath;
    }

    public static async Task GeneratePdfAsync(string jsonPayloadPath, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        var htmlOutputPath = Path.ChangeExtension(outputPdfPath, ".html");
        await GenerateHtmlAsync(jsonPayloadPath, htmlOutputPath, cancellationToken);
        await GeneratePdfFromHtmlAsync(htmlOutputPath, outputPdfPath, cancellationToken);
    }

    public static async Task GeneratePdfFromHtmlAsync(string htmlOutputPath, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(htmlOutputPath)) throw new FileNotFoundException("No se encontró el reporte HTML.", htmlOutputPath);
        var edgePath = FindEdge();
        if (edgePath is null)
            throw new FileNotFoundException("Microsoft Edge no se encontró en el sistema.");

        await RunProcessAsync(edgePath, outputPdfPath, htmlOutputPath, cancellationToken);

        if (!File.Exists(outputPdfPath))
            throw new InvalidOperationException("Edge terminó, pero no generó el archivo PDF.");
    }

    private static string BuildReportHtml(string jsonPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            var dateStr = root.TryGetProperty("Date", out var dProp) ? dProp.GetString() : DateTime.Today.ToString("yyyy-MM-dd");
            var metrics = root.TryGetProperty("Metrics", out var mProp) ? mProp : default;

            int totalProjects = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("TotalProjects", out var tp) ? tp.GetInt32() : 0;
            int totalActivities = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("TotalActivities", out var ta) ? ta.GetInt32() : 0;
            int lagging = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("LaggingActivities", out var la) ? la.GetInt32() : 0;
            int pending = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("PendingToday", out var pen) ? pen.GetInt32() : 0;
            int completed = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("CompletedToday", out var com) ? com.GetInt32() : 0;
            int inReview = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("ProjectsInReview", out var ir) ? ir.GetInt32() : 0;
            int unassigned = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("UnassignedActivities", out var ua) ? ua.GetInt32() : 0;
            int missingChecklist = metrics.ValueKind != JsonValueKind.Undefined && metrics.TryGetProperty("MissingChecklistActivities", out var mc) ? mc.GetInt32() : 0;

            string projectsHtml = "";
            if (root.TryGetProperty("Projects", out var projArr) && projArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in projArr.EnumerateArray())
                {
                    string name = E(p.TryGetProperty("ProjectName", out var pn) ? pn.GetString() : null);
                    bool isCrit = p.TryGetProperty("IsCritical", out var ic) && ic.GetBoolean();
                    bool isSusp = p.TryGetProperty("IsSuspended", out var isb) && isb.GetBoolean();
                    string badge = isCrit ? "<span class=\"badge crit\">Crítico</span>" : (isSusp ? "<span class=\"badge susp\">Suspendido</span>" : "<span class=\"badge norm\">Activo</span>");

                    projectsHtml += $@"
                    <div class=""row-item"">
                        <span class=""proj-name"">{name}</span> {badge}
                    </div>";
                }
            }
            if (string.IsNullOrEmpty(projectsHtml)) projectsHtml = "<div class=\"row-item\">No hay proyectos registrados.</div>";

            string peopleHtml = "";
            if (root.TryGetProperty("People", out var peopArr) && peopArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in peopArr.EnumerateArray())
                {
                    string name = E(p.TryGetProperty("Name", out var pn) ? pn.GetString() : null);
                    int act = p.TryGetProperty("ActivitiesToday", out var ac) ? ac.GetInt32() : 0;
                    int penP = p.TryGetProperty("PendingToday", out var pp) ? pp.GetInt32() : 0;
                    int lagP = p.TryGetProperty("LaggingActivities", out var lp) ? lp.GetInt32() : 0;

                    peopleHtml += $@"
                    <tr>
                        <td style=""font-weight: 600;"">{name}</td>
                        <td class=""center"">{act}</td>
                        <td class=""center"">{penP}</td>
                        <td class=""center"">{lagP}</td>
                    </tr>";
                }
            }

            return $@"<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <title>Resumen Operativo ANFETA</title>
    <style>
        @page {{
            size: A4;
            margin: 0;
        }}
        body {{
            background-color: #0c161f;
            color: #f1f7fc;
            font-family: 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            margin: 0;
            padding: 32px;
            -webkit-print-color-adjust: exact;
        }}
        .container {{
            max-width: 800px;
            margin: 0 auto;
            background-color: #111e2a;
            border: 1px solid #263b4b;
            border-radius: 12px;
            padding: 28px;
        }}
        .header {{ margin-bottom: 24px; border-bottom: 1px solid #263b4b; padding-bottom: 16px; }}
        .header h3 {{ color: #48bef5; font-size: 11px; margin: 0; text-transform: uppercase; letter-spacing: 1px; }}
        .header h1 {{ color: #f1f7fc; font-size: 24px; margin: 6px 0; font-weight: 700; }}
        .header p {{ color: #9db1c2; font-size: 12px; margin: 0; }}
        
        .kpi-grid {{
            display: grid;
            grid-template-columns: repeat(4, 1fr);
            gap: 10px;
            margin-bottom: 20px;
        }}
        .kpi-card {{
            background-color: #14202b;
            border: 1px solid #263b4b;
            border-radius: 8px;
            padding: 12px;
        }}
        .kpi-val {{ font-size: 22px; font-weight: bold; }}
        .kpi-lbl {{ font-size: 9px; color: #9db1c2; margin-top: 4px; text-transform: uppercase; letter-spacing: 0.5px; }}
        
        .card {{
            background-color: #14202b;
            border: 1px solid #263b4b;
            border-radius: 9px;
            padding: 16px;
            margin-bottom: 16px;
        }}
        .card-title {{ font-size: 12px; font-weight: bold; color: #ebf3f9; margin-bottom: 12px; border-left: 4px solid #1db0f2; padding-left: 8px; text-transform: uppercase; letter-spacing: 0.5px; }}
        
        .row-item {{
            background-color: #0c161f;
            border-left: 3px solid #f5b937;
            padding: 10px 12px;
            margin-bottom: 8px;
            border-radius: 6px;
            font-size: 13px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }}
        .proj-name {{ font-weight: 600; color: #f1f7fc; }}
        
        .badge {{ font-size: 10px; padding: 3px 8px; border-radius: 4px; font-weight: 600; text-transform: uppercase; }}
        .badge.crit {{ background: #ff5f60; color: #fff; }}
        .badge.susp {{ background: #f5b937; color: #111e2a; }}
        .badge.norm {{ background: #2abf8e; color: #111e2a; }}

        table {{ width: 100%; border-collapse: collapse; font-size: 13px; }}
        th {{ text-align: left; color: #9db1c2; border-bottom: 1px solid #263b4b; padding-bottom: 8px; font-weight: 600; text-transform: uppercase; font-size: 10px; letter-spacing: 0.5px; }}
        td {{ padding: 10px 0; border-bottom: 1px solid #1a2c3d; color: #d4e2ed; }}
        .center {{ text-align: center; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h3>ANFETA · Resumen Operativo</h3>
            <h1>{dateStr}</h1>
            <p>Métricas reales calculadas por Avance Diario</p>
        </div>

        <div class=""kpi-grid"">
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#1db0f2;"">{totalProjects}</div><div class=""kpi-lbl"">Proyectos</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#5aa5ff;"">{totalActivities}</div><div class=""kpi-lbl"">Actividades</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#ff5860;"">{lagging}</div><div class=""kpi-lbl"">Rezagadas</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#f5b937;"">{pending}</div><div class=""kpi-lbl"">Pendientes</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#2acf8e;"">{completed}</div><div class=""kpi-lbl"">Terminadas</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#b473ff;"">{inReview}</div><div class=""kpi-lbl"">En Revisión</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#ff9146;"">{unassigned}</div><div class=""kpi-lbl"">Sin Responsable</div></div>
            <div class=""kpi-card""><div class=""kpi-val"" style=""color:#8797a5;"">{missingChecklist}</div><div class=""kpi-lbl"">Sin Checklist</div></div>
        </div>

        <div class=""card"">
            <div class=""card-title"">Estado de Proyectos</div>
            {projectsHtml}
        </div>

        <div class=""card"">
            <div class=""card-title"">Carga por Persona</div>
            <table>
                <tr><th>Responsable</th><th class=""center"">Act.</th><th class=""center"">Pend.</th><th class=""center"">Rez.</th></tr>
                {peopleHtml}
            </table>
        </div>
    </div>
</body>
</html>";
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("No se pudo interpretar el snapshot para generar el reporte.", ex);
        }
    }

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    private static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static async Task RunProcessAsync(string fileName, string outputPdfPath, string htmlOutputPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--disable-gpu");
        info.ArgumentList.Add("--run-all-compositor-stages-before-draw");
        info.ArgumentList.Add("--no-pdf-header-footer");
        info.ArgumentList.Add("--print-to-pdf=" + outputPdfPath);
        info.ArgumentList.Add(new Uri(htmlOutputPath).AbsoluteUri);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar {Path.GetFileName(fileName)}.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();

        if (process.ExitCode != 0)
        {
            var log = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"[{Path.GetFileName(fileName)}] {log}");
        }
    }
}
