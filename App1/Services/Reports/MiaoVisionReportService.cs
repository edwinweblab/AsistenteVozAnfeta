using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Collections.Generic;
using Anfeta.UI.Models.DailyAi;

namespace Anfeta.UI.Services.Reports;

public sealed class MiaoVisionReportService
{
    public async Task<string> GenerateDashboardAsync(DailyAiSnapshot snapshot, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(MiaoVisionInstallerService.ExecutablePath))
            throw new InvalidOperationException("Miao Vision no está instalado.");
        Directory.CreateDirectory(outputDirectory);
        var csvPath = Path.Combine(outputDirectory, "miao_operational_summary.csv");
        var contextPath = Path.Combine(outputDirectory, "miao_context.json");
        var specPath = Path.Combine(outputDirectory, "miao_anfeta_dashboard.yaml");
        var htmlPath = Path.Combine(outputDirectory, "Resumen_Anfeta_Miao.html");

        await File.WriteAllTextAsync(csvPath, BuildOperationalCsv(snapshot), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(specPath, BuildSpec(snapshot), new UTF8Encoding(false), cancellationToken);

        await RunAsync(new[] { "data", "analyze", csvPath, "--intent", "Comparar actividades, pendientes y rezagos por proyecto", "--output", contextPath, "--compact" }, cancellationToken);
        await RunAsync(new[] { "render", "report", "--input", csvPath, "--spec", specPath, "--context", contextPath, "--format", "html", "--theme", "standard-dark", "--output", htmlPath }, cancellationToken);

        if (!File.Exists(htmlPath)) throw new InvalidOperationException("Miao terminó sin crear la visualización HTML.");
        await EnhanceHtmlAsync(htmlPath, snapshot, cancellationToken);
        return htmlPath;
    }

    private static async Task EnhanceHtmlAsync(string htmlPath, DailyAiSnapshot snapshot, CancellationToken cancellationToken)
    {
        var html = await File.ReadAllTextAsync(htmlPath, cancellationToken);
        var m = snapshot.Metrics;
        static string P(int value, int total) => total <= 0 ? "0%" : (value * 100d / total).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        var explanation = $"""
<section style="margin:18px 0;padding:18px 20px;border:1px solid #2a3444;border-radius:10px;background:#161b26;color:#e8edf5">
  <div style="font-size:12px;color:#7eb8f7;text-transform:uppercase;font-weight:700">Lectura rápida</div>
  <div style="font-size:18px;font-weight:700;margin-top:7px">{m.TotalActivities} actividades en {m.TotalProjects} proyectos</div>
  <div style="margin-top:8px;color:#bac4d3;line-height:1.55">Pendientes: <b>{m.PendingToday}</b> ({P(m.PendingToday, m.TotalActivities)}) · Rezagadas: <b>{m.LaggingActivities}</b> ({P(m.LaggingActivities, m.TotalActivities)}) · Terminadas hoy: <b>{m.CompletedToday}</b> · En revisión: <b>{m.ProjectsInReview}</b> · Sin responsable: <b>{m.UnassignedActivities}</b>.</div>
  <div style="margin-top:7px;color:#8e9bad;font-size:12px">El número encima de cada barra es la cantidad exacta. Coloca el cursor sobre una barra para consultar el proyecto y su valor.</div>
</section>
<div class="kpi-grid">
""";
        html = html.Replace("<div class=\"kpi-grid\">", explanation, StringComparison.Ordinal);
        html = html.Replace("View evidence", "Ver datos", StringComparison.Ordinal)
                   .Replace("No data for the current view", "No hay datos para esta vista", StringComparison.Ordinal)
                   .Replace("'<div class=\"miao-bigvalue\"><div style=\"font-size:42px;font-weight:700\">' + escapeHtml(value == null ? '—' : String(value)) + '</div><div class=\"miao-view-derived\">Current filter</div></div>'",
                            "'<div class=\"miao-bigvalue\"><div style=\"font-size:42px;font-weight:700\">' + escapeHtml(value == null ? '—' : String(value)) + '</div><div class=\"miao-view-derived\">' + escapeHtml(chart.title || 'Cantidad') + '</div></div>'", StringComparison.Ordinal);
        const string barNeedle = "'\" rx=\"3\" fill=\"' + color(index) + '\" />' +\n          miaoData.renderBarAxisLabel";
        const string barReplacement = "'\" rx=\"3\" fill=\"' + color(index) + '\" />' +\n          '<text x=\"' + fixed(x + barWidth / 2) + '\" y=\"' + fixed(Math.max(15, y - 7)) + '\" text-anchor=\"middle\" fill=\"#eef4fb\" font-size=\"12\" font-weight=\"700\">' + value + '</text>' +\n          miaoData.renderBarAxisLabel";
        html = html.Replace(barNeedle, barReplacement, StringComparison.Ordinal);
        const string compactCss = """
<style id="anfeta-compact-layout">
  @page{size:A4;margin:0}
  html,body{margin:0!important;padding:0!important;background:#0d1119!important;-webkit-print-color-adjust:exact!important;print-color-adjust:exact!important}
  .miao-viz-report{max-width:1480px!important;padding:28px 24px 42px!important}
  .chart-card{margin:14px 0!important;padding:18px 20px 14px!important}
  .kpi-grid{grid-template-columns:repeat(6,minmax(120px,1fr))!important}
  .kpi-grid .miao-bigvalue{padding:10px 14px 14px!important}
  .anfeta-chart-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px;margin-top:14px}
  .anfeta-chart-grid>.chart-card{margin:0!important;min-width:0}
  .anfeta-chart-grid .miao-chart-svg{height:330px!important;width:100%!important}
  .anfeta-chart-grid h2{font-size:18px;margin-bottom:6px}
  .anfeta-chart-grid .chart-caption{font-size:10px;margin-top:4px}
  @media(max-width:1050px){.kpi-grid{grid-template-columns:repeat(3,1fr)!important}.anfeta-chart-grid{grid-template-columns:1fr}.anfeta-chart-grid .miao-chart-svg{height:auto!important}}
  @media(max-width:650px){.miao-viz-report{padding:18px 12px 30px!important}.kpi-grid{grid-template-columns:repeat(2,1fr)!important}}
  @media print{.miao-viz-report{box-sizing:border-box;max-width:none!important;width:100%!important;padding:8mm!important}.chart-card{break-inside:avoid;page-break-inside:avoid}body{min-height:100vh}}
</style>
""";
        html = html.Replace("</head>", compactCss + "</head>", StringComparison.Ordinal);
        const string firstChart = "<section class=\"chart-card\" data-miao-chart=\"activities_by_project\">";
        var wrappedCharts = html.Contains(firstChart, StringComparison.Ordinal);
        if (wrappedCharts) html = html.Replace(firstChart, "<div class=\"anfeta-chart-grid\">" + firstChart, StringComparison.Ordinal);
        var history = await LoadHistoryAsync(Path.GetDirectoryName(htmlPath)!, snapshot, cancellationToken);
        var details = (wrappedCharts ? "</div>" : string.Empty) + BuildOperationalDetails(snapshot, history);
        html = html.Replace("  </main>", details + "\n  </main>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken);
    }

    private static string BuildOperationalDetails(DailyAiSnapshot snapshot, IReadOnlyList<DailyAiSnapshot> history)
    {
        static string E(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);
        var m = snapshot.Metrics;
        var sb = new StringBuilder($"""
<section style="margin-top:16px;padding:14px 16px;border:1px solid #2a3444;border-radius:10px;background:#161b26">
  <div style="font-size:12px;color:#7eb8f7;font-weight:700;text-transform:uppercase;margin-bottom:10px">Distribución por estado</div>
  <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(125px,1fr));gap:8px">
    <div style="padding:9px;background:#111722;border-radius:6px"><b style="color:#f5b937">{m.PendingToday}</b><div style="font-size:11px;color:#9ba8b8">Pendientes</div></div>
    <div style="padding:9px;background:#111722;border-radius:6px"><b style="color:#b473ff">{m.ProjectsInReview}</b><div style="font-size:11px;color:#9ba8b8">En revisión</div></div>
    <div style="padding:9px;background:#111722;border-radius:6px"><b style="color:#f29a52">{m.SuspendedProjects}</b><div style="font-size:11px;color:#9ba8b8">Suspendidos</div></div>
    <div style="padding:9px;background:#111722;border-radius:6px"><b style="color:#2acf8e">{m.CompletedToday}</b><div style="font-size:11px;color:#9ba8b8">Terminadas hoy</div></div>
    <div style="padding:9px;background:#111722;border-radius:6px"><b style="color:#ff7279">{m.UnassignedActivities}</b><div style="font-size:11px;color:#9ba8b8">Sin responsable</div></div>
  </div>
</section>
<section style="margin-top:24px;display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:16px">
  <div style="padding:18px;border:1px solid #2a3444;border-radius:10px;background:#161b26">
    <div style="font-size:12px;color:#7eb8f7;font-weight:700;text-transform:uppercase">Carga por responsable</div>
    <table style="width:100%;border-collapse:collapse;margin-top:12px;color:#e8edf5;font-size:13px">
      <thead><tr style="color:#8e9bad;text-align:left"><th style="padding:7px">Responsable</th><th>Act.</th><th>Pend.</th><th>Rez.</th><th>Proy.</th></tr></thead><tbody>
""");
        foreach (var person in snapshot.People.OrderByDescending(x => x.PendingToday).ThenByDescending(x => x.ActivitiesToday).Take(12))
            sb.Append($"<tr style=\"border-top:1px solid #273142\"><td style=\"padding:8px 7px;font-weight:600\">{E(person.Name)}</td><td>{person.ActivitiesToday}</td><td>{person.PendingToday}</td><td>{person.LaggingActivities}</td><td>{person.ProjectsCount}</td></tr>");
        sb.Append("""
      </tbody></table>
  </div>
  <div style="padding:18px;border:1px solid #2a3444;border-radius:10px;background:#161b26">
    <div style="font-size:12px;color:#ff7279;font-weight:700;text-transform:uppercase">Proyectos que requieren atención</div>
    <div style="margin-top:12px;display:grid;gap:8px">
""");
        var allCritical = snapshot.Projects.Where(x => x.IsCritical).OrderByDescending(x => x.PendingToday).ToList();
        var critical = allCritical.Take(8).ToList();
        if (critical.Count == 0)
            sb.Append("<div style=\"color:#8e9bad\">No hay proyectos críticos con las reglas actuales.</div>");
        foreach (var project in critical)
        {
            var reasons = project.CriticalReasons.Count == 0 ? "Requiere revisión" : string.Join(" · ", project.CriticalReasons);
            sb.Append($"<div style=\"padding:10px 12px;border-left:3px solid #ff5964;background:#111722;border-radius:6px\"><div style=\"font-weight:700;color:#eef4fb\">{E(project.ProjectName)} <span style=\"color:#ffb35c;font-weight:600\">· {project.PendingToday} pendiente(s)</span></div><div style=\"margin-top:4px;color:#aab5c4;font-size:12px\">{E(reasons)}</div></div>");
        }
        if (allCritical.Count > critical.Count)
            sb.Append($"<div style=\"padding:7px;color:#8e9bad;font-size:12px\">+ {allCritical.Count - critical.Count} proyecto(s) adicional(es). Consulta «Ver datos» para el detalle completo.</div>");
        sb.Append("</div></div></section>");
        sb.Append(BuildHistory(snapshot, history));
        return sb.ToString();
    }

    private static async Task<IReadOnlyList<DailyAiSnapshot>> LoadHistoryAsync(string currentDirectory,
        DailyAiSnapshot current, CancellationToken cancellationToken)
    {
        var values = new List<DailyAiSnapshot> { current };
        var root = Directory.GetParent(currentDirectory)?.FullName;
        if (root is null || !Directory.Exists(root)) return values;
        foreach (var file in Directory.EnumerateFiles(root, "anfeta_daily_*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var item = JsonSerializer.Deserialize<DailyAiSnapshot>(json);
                if (item is not null && item.Date.Date != current.Date.Date) values.Add(item);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        return values.GroupBy(x => x.Date.Date).Select(x => x.OrderByDescending(y => y.GeneratedAt).First())
            .OrderByDescending(x => x.Date).Take(30).ToList();
    }

    private static string BuildHistory(DailyAiSnapshot current, IReadOnlyList<DailyAiSnapshot> history)
    {
        var ordered = history.OrderByDescending(x => x.Date).ToList();
        var previous = ordered.FirstOrDefault(x => x.Date.Date < current.Date.Date);
        static string Delta(int now, int before)
        {
            var delta = now - before;
            return delta == 0 ? "sin cambio" : delta > 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);
        }
        var comparison = previous is null
            ? "Aún no existe otro día guardado para comparar."
            : $"Contra {previous.Date:dd/MM}: pendientes {Delta(current.Metrics.PendingToday, previous.Metrics.PendingToday)} · rezagadas {Delta(current.Metrics.LaggingActivities, previous.Metrics.LaggingActivities)} · actividades {Delta(current.Metrics.TotalActivities, previous.Metrics.TotalActivities)}.";
        var sb = new StringBuilder($"""
<section style="margin-top:16px;padding:16px 18px;border:1px solid #2a3444;border-radius:10px;background:#161b26;color:#e8edf5">
  <div style="display:flex;justify-content:space-between;gap:12px;align-items:center;flex-wrap:wrap">
    <div><div style="font-size:12px;color:#7eb8f7;font-weight:700;text-transform:uppercase">Comparativo histórico</div><div style="margin-top:5px;color:#bac4d3">{comparison}</div></div>
    <div style="display:flex;gap:6px"><button onclick="anfetaHistory(1)">Hoy</button><button onclick="anfetaHistory(7)">7 días</button><button onclick="anfetaHistory(30)">30 días</button></div>
  </div>
  <div style="overflow-x:auto;margin-top:12px"><table style="width:100%;border-collapse:collapse;font-size:12px"><thead><tr style="color:#8e9bad;text-align:left"><th>Fecha</th><th>Proyectos</th><th>Actividades</th><th>Pendientes</th><th>Rezagadas</th><th>Terminadas</th><th>Sin responsable</th></tr></thead><tbody>
""");
        foreach (var item in ordered)
        {
            var age = Math.Max(0, (current.Date.Date - item.Date.Date).Days);
            var m = item.Metrics;
            sb.Append($"<tr data-history-age=\"{age}\" style=\"border-top:1px solid #273142\"><td style=\"padding:8px 0;font-weight:600\">{item.Date:dd/MM/yyyy}</td><td>{m.TotalProjects}</td><td>{m.TotalActivities}</td><td>{m.PendingToday}</td><td>{m.LaggingActivities}</td><td>{m.CompletedToday}</td><td>{m.UnassignedActivities}</td></tr>");
        }
        sb.Append("""
  </tbody></table></div>
  <div style="margin-top:8px;color:#7f8b9b;font-size:11px">Solo se muestran snapshots realmente guardados por ANFETA; las fechas faltantes no se rellenan.</div>
</section>
<script>function anfetaHistory(days){document.querySelectorAll('[data-history-age]').forEach(function(row){row.style.display=Number(row.dataset.historyAge)<days?'table-row':'none';});}</script>
""");
        return sb.ToString();
    }

    private static string BuildOperationalCsv(DailyAiSnapshot snapshot)
    {
        static string Q(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Project,Domain,Area,Activities,Pending,Lagging,Completed,InReview,Suspended,Unassigned,ProgressPct\r\n");
        foreach (var project in snapshot.Projects.OrderByDescending(x => x.ActivitiesToday))
        {
            var projectActivities = snapshot.Activities.Where(x => string.Equals(x.ProjectName, project.ProjectName, StringComparison.OrdinalIgnoreCase)).ToList();
            sb.AppendLine(string.Join(',', new object?[] { project.ProjectName, project.Domain, project.Area,
                project.ActivitiesToday, project.PendingToday, projectActivities.Count(x => x.IsLagging),
                project.CompletedToday, project.IsInReview ? 1 : 0, project.IsSuspended ? 1 : 0,
                projectActivities.Count(x => x.IsUnassigned), project.ProgressTodayPct }.Select(Q)));
        }
        return sb.ToString();
    }

    private static string BuildSpec(DailyAiSnapshot snapshot)
    {
        var progressChart = snapshot.Projects.Any(x => x.ProgressTodayPct > 0) ? """
  - id: progress_by_project
    type: bar
    title: Porcentaje de avance diario por proyecto · Top 12
    data:
      transform:
        - type: sort
          field: ProgressPct
          order: desc
        - type: limit
          value: 12
    encoding:
      x: { field: Project, type: nominal }
      y: { field: ProgressPct, type: quantitative }
""" : string.Empty;
        return $$"""
title: ANFETA · Resumen operativo {{snapshot.Date:dd/MM/yyyy}}
insights: []
charts:
  - id: total_activities
    type: bigvalue
    title: Actividades del día
    data:
      transform:
        - type: aggregate
          measures:
            - field: Activities
              op: sum
              as: total_Activities
    encoding:
      value: { field: total_Activities, type: quantitative }
  - id: total_pending
    type: bigvalue
    title: Pendientes
    data:
      transform:
        - type: aggregate
          measures:
            - field: Pending
              op: sum
              as: total_Pending
    encoding:
      value: { field: total_Pending, type: quantitative }
  - id: total_lagging
    type: bigvalue
    title: Rezagadas
    data:
      transform:
        - type: aggregate
          measures:
            - field: Lagging
              op: sum
              as: total_Lagging
    encoding:
      value: { field: total_Lagging, type: quantitative }
  - id: total_completed
    type: bigvalue
    title: Terminadas hoy
    data:
      transform:
        - type: aggregate
          measures:
            - field: Completed
              op: sum
              as: total_Completed
    encoding:
      value: { field: total_Completed, type: quantitative }
  - id: total_review
    type: bigvalue
    title: Proyectos en revisión
    data:
      transform:
        - type: aggregate
          measures:
            - field: InReview
              op: sum
              as: total_InReview
    encoding:
      value: { field: total_InReview, type: quantitative }
  - id: total_unassigned
    type: bigvalue
    title: Actividades sin responsable
    data:
      transform:
        - type: aggregate
          measures:
            - field: Unassigned
              op: sum
              as: total_Unassigned
    encoding:
      value: { field: total_Unassigned, type: quantitative }
  - id: activities_by_project
    type: bar
    title: Cantidad de actividades por proyecto · Top 12
    data:
      transform:
        - type: sort
          field: Activities
          order: desc
        - type: limit
          value: 12
    encoding:
      x: { field: Project, type: nominal }
      y: { field: Activities, type: quantitative }
  - id: pending_by_project
    type: bar
    title: Cantidad de pendientes por proyecto · Top 12
    data:
      transform:
        - type: sort
          field: Pending
          order: desc
        - type: limit
          value: 12
    encoding:
      x: { field: Project, type: nominal }
      y: { field: Pending, type: quantitative }
  - id: activities_by_area
    type: bar
    title: Cantidad de actividades por área
    data:
      transform:
        - type: aggregate
          groupBy:
            - Area
          measures:
            - field: Activities
              op: sum
              as: total_Activities
        - type: sort
          field: total_Activities
          order: desc
    encoding:
      x: { field: Area, type: nominal }
      y: { field: total_Activities, type: quantitative }
{{progressChart}}
""";
    }

    private static async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(MiaoVisionInstallerService.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = MiaoVisionInstallerService.InstallDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows no pudo iniciar Miao Vision.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
    }
}
