using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Views;

public sealed partial class CommandsView : Page
{
    private const string ApisTxt = @"
GUIA DE COMANDOS – ASISTENTE WEBLAB (LITE)

MÓDULO: ACTIVIDADES
CMD-ACT-001 — Listar todas las actividades | Frase: ""Muéstrame todas las actividades""
CMD-ACT-002 — Buscar actividades por texto | Frase: ""Busca actividades con contrato""
CMD-ACT-003 — Obtener actividades por rango de fechas | Frase: ""Actividades de esta semana""
CMD-ACT-004 — Obtener actividad por ID | Frase: ""Abrir actividad por ID""
CMD-ACT-005 — Crear actividad | Frase: ""Crea una actividad nueva""
CMD-ACT-006 — Crear actividad con tarjet | Frase: ""Crear actividad desde tarjeta""
CMD-ACT-007 — Crear actividad vacía | Frase: ""Crea una actividad en blanco""
CMD-ACT-008 — Actualizar actividad | Frase: ""Actualiza esta actividad""
CMD-ACT-009 — Eliminar actividad | Frase: ""Elimina esta actividad""

ACCIONES BATCH – ACTIVIDADES
CMD-ACT-BATCH-001 — Reprogramar atrasados | Frase: ""Reprograma actividades atrasadas""
CMD-ACT-BATCH-002 — Mover FTF a mañana | Frase: ""Mueve FTF a mañana""
CMD-ACT-BATCH-003 — Asignar horarios largos hoy | Frase: ""Asigna horarios largos hoy""
CMD-ACT-BATCH-004 — Mover fechas | Frase: ""Mueve fechas de actividades""
CMD-ACT-BATCH-005 — Actualizar propiedades masivas | Frase: ""Actualiza actividades en lote""

MÓDULO: PENDIENTES
CMD-PEN-001 — Crear pendiente | Frase: ""Agrega un pendiente""
CMD-PEN-002 — Reordenar pendientes | Frase: ""Reordena pendientes""
CMD-PEN-003 — Actualizar pendiente | Frase: ""Marca pendiente como hecho""
CMD-PEN-004 — Eliminar pendiente | Frase: ""Elimina pendiente""

MÓDULO: DROPBOX / NOTION FILES
CMD-DBX-001 — Sincronización inicial | Frase: ""Inicia sincronización completa""
CMD-DBX-002 — Delta sync | Frase: ""Sincroniza cambios recientes""
CMD-DBX-003 — Listar archivos | Frase: ""Lista archivos""
CMD-DBX-004 — Obtener metadata | Frase: ""Ver info del archivo""
CMD-DBX-005 — Breadcrumbs | Frase: ""Muéstrame la ruta del archivo""
CMD-DBX-006 — Tree de carpetas | Frase: ""Muestra árbol de carpetas""
CMD-DBX-007 — Estadísticas | Frase: ""Estadísticas de Dropbox""
CMD-DBX-008 — Buscar archivos simple | Frase: ""Busca archivos""
CMD-DBX-009 — Buscar archivos con links | Frase: ""Busca archivos con Notion""
CMD-DBX-010 — Asegurar link compartido | Frase: ""Genera link del archivo""
CMD-DBX-011 — Recomputar metadata | Frase: ""Recalcula metadata""
CMD-DBX-012 — Subir archivo | Frase: ""Sube este archivo""
CMD-DBX-013 — Crear carpeta | Frase: ""Crea una carpeta""
CMD-DBX-014 — Renombrar nodo | Frase: ""Renombra archivo""
CMD-DBX-015 — Eliminar nodo | Frase: ""Elimina archivo""

MÓDULO: GOOGLE AUTH & CALENDAR
CMD-GGL-001 — Iniciar auth Google | Frase: ""Conecta Google""
CMD-GGL-002 — Logout Google | Frase: ""Desconecta Google""
CMD-GGL-003 — Estado Google | Frase: ""¿Estoy conectado a Google?""
CMD-GGL-004 — Crear evento calendario | Frase: ""Crea evento en calendario""
CMD-GGL-005 — Actualizar evento | Frase: ""Actualiza el evento""
CMD-GGL-006 — Listar eventos | Frase: ""Muéstrame mis eventos""
CMD-GGL-007 — Eliminar evento | Frase: ""Elimina el evento""

MÓDULO: OPCIONES
CMD-OPT-001 — Obtener opciones | Frase: ""Carga opciones del sistema""

MÓDULO: PRESENCE
CMD-PRE-001 — Usuarios online | Frase: ""¿Quién está en línea?""

MÓDULO: PROYECTOS
CMD-PRJ-001 — Listar proyectos | Frase: ""Lista proyectos""
CMD-PRJ-002 — Buscar proyectos | Frase: ""Busca un proyecto""
CMD-PRJ-003 — Obtener proyecto por ID | Frase: ""Abrir proyecto por ID""
CMD-PRJ-004 — Crear proyecto | Frase: ""Crea un proyecto""
CMD-PRJ-005 — Actualizar proyecto | Frase: ""Actualiza el proyecto""
CMD-PRJ-006 — Eliminar proyecto | Frase: ""Elimina el proyecto""

MÓDULO: RECORDATORIOS
CMD-REC-001 — Crear recordatorio | Frase: ""Crea un recordatorio""
CMD-REC-002 — Listar recordatorios | Frase: ""Muéstrame recordatorios""
CMD-REC-003 — Marcar como enviado | Frase: ""Marca recordatorio como enviado""
CMD-REC-004 — Eliminar recordatorio | Frase: ""Elimina el recordatorio""

MÓDULO: REPORTES
CMD-REP-001 — Listar eventos | Frase: ""Eventos del sistema""
CMD-REP-002 — Resumen por periodo | Frase: ""Resumen del mes""
CMD-REP-003 — Resumen personalizado | Frase: ""Resumen personalizado""
CMD-REP-004 — Últimos registros | Frase: ""Últimos movimientos""
CMD-REP-005 — Comprobatoria | Frase: ""Comprobatoria por colaborador""
CMD-REP-006 — Tareas rezagadas | Frase: ""Tareas rezagadas""
CMD-REP-007 — Revisiones por fecha | Frase: ""Revisiones por fecha""

MÓDULO: REVISIONES
CMD-REV-001 — Listar revisiones | Frase: ""Lista revisiones""
CMD-REV-002 — Revisiones por actividad | Frase: ""Revisiones de la actividad""
CMD-REV-003 — Buscar revisiones | Frase: ""Busca revisiones""
CMD-REV-004 — Revisiones por fechas | Frase: ""Revisiones por rango de fechas""
CMD-REV-005 — Resumen día actual | Frase: ""Resumen de revisiones de hoy""
CMD-REV-006 — Mover fechas (batch) | Frase: ""Mueve fechas de revisiones""
CMD-REV-007 — Confirmar revisión | Frase: ""Confirma revisión""
CMD-REV-008 — Ordenar revisiones | Frase: ""Ordena revisiones""
CMD-REV-009 — Obtener revisión por ID | Frase: ""Abrir revisión por ID""
CMD-REV-010 — Duplicar revisión | Frase: ""Duplica revisión""
CMD-REV-011 — Migrar assignees | Frase: ""Migrar responsables""
CMD-REV-012 — Preview revisión | Frase: ""Ver preview de revisión""
CMD-REV-013 — Refresh preview | Frase: ""Actualiza preview de revisión""
CMD-REV-014 — Crear revisión | Frase: ""Crea una revisión""
CMD-REV-015 — Actualizar revisión | Frase: ""Actualiza la revisión""
CMD-REV-016 — Eliminar revisión | Frase: ""Elimina la revisión""

MÓDULO: USUARIOS
CMD-USR-001 — Buscar usuarios | Frase: ""Busca un usuario""

MÓDULO: SOCKET / TIEMPO REAL
CMD-SKT-001 — Presence ping | Frase: ""Mantener sesión activa""
CMD-SKT-002 — Enviar mensaje de chat | Frase: ""Envía mensaje a un usuario""
";

    private List<CommandModule> _modules = new();
    private CommandModule? _selectedModule;
    private string _query = "";

    public string SelectedModuleTitle => _selectedModule?.Name ?? "Selecciona un módulo";
    public string SelectedModuleSubtitle => _selectedModule is null
        ? "Elige un módulo para ver sus comandos."
        : $"{_selectedModule.Commands.Count} comandos detectados.";

    public CommandsView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += CommandsView_Loaded;
    }

    private void CommandsView_Loaded(object sender, RoutedEventArgs e)
    {
        _modules = ParseModules(ApisTxt);

        ModulesCombo.DisplayMemberPath = "Name";
        ModulesCombo.ItemsSource = _modules;
        ModulesCombo.SelectedIndex = _modules.Count > 0 ? 0 : -1;

        ApplyFilter();
        UpdateHeaderBindings();
    }

    private void ModulesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedModule = ModulesCombo.SelectedItem as CommandModule;
        ApplyFilter();
        UpdateHeaderBindings();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = (SearchBox.Text ?? "").Trim();
        ApplyFilter();
        UpdateHeaderBindings();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _query = "";
        if (_modules.Count > 0) ModulesCombo.SelectedIndex = 0;
        ApplyFilter();
        UpdateHeaderBindings();
    }

    private void ApplyFilter()
    {
        _selectedModule = ModulesCombo.SelectedItem as CommandModule;

        if (_modules.Count == 0)
        {
            CommandsList.ItemsSource = Array.Empty<CommandItem>();
            return;
        }

        var q = (_query ?? "").Trim().ToLowerInvariant();

        // Base: módulo seleccionado o todo
        var baseList = _selectedModule?.Commands ?? _modules.SelectMany(m => m.Commands).ToList();

        if (string.IsNullOrWhiteSpace(q))
        {
            CommandsList.ItemsSource = baseList;
            return;
        }

        bool Match(CommandItem c) =>
            (c.Id ?? "").ToLowerInvariant().Contains(q) ||
            (c.Module ?? "").ToLowerInvariant().Contains(q) ||
            (c.Title ?? "").ToLowerInvariant().Contains(q) ||
            (c.Endpoint ?? "").ToLowerInvariant().Contains(q) ||
            (c.Function ?? "").ToLowerInvariant().Contains(q) ||
            (c.ExamplesLine ?? "").ToLowerInvariant().Contains(q) ||
            (c.MinParamsLine ?? "").ToLowerInvariant().Contains(q);

        // 1) intenta dentro del módulo
        var filtered = baseList.Where(Match).ToList();

        // 2) fallback global
        if (filtered.Count == 0)
        {
            filtered = _modules.SelectMany(m => m.Commands).Where(Match).ToList();
        }

        CommandsList.ItemsSource = filtered;
    }

    private void UpdateHeaderBindings()
    {
        DataContext = null;
        DataContext = this;
    }

    // -----------------------------
    // PARSER
    // -----------------------------
    private static List<CommandModule> ParseModules(string text)
    {
        text = text.Replace("\r\n", "\n");

        // Detecta líneas tipo: MÓDULO: X  /  MODULO: X
        var modRegex = new Regex(@"(?im)^\s*m[oó]dulo\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
        var modMatches = modRegex.Matches(text);

        if (modMatches.Count == 0)
        {
            var cmds = ParseCommandsFromModuleChunk("General", text);
            return cmds.Count > 0
                ? new List<CommandModule> { new CommandModule { Name = "General", Commands = cmds } }
                : new List<CommandModule>();
        }

        var ranges = new List<(string name, int start, int end)>();

        for (int i = 0; i < modMatches.Count; i++)
        {
            var name = modMatches[i].Groups[1].Value.Trim();
            var start = modMatches[i].Index;
            var end = (i + 1 < modMatches.Count) ? modMatches[i + 1].Index : text.Length;
            ranges.Add((name, start, end));
        }

        var result = new List<CommandModule>();
        foreach (var r in ranges)
        {
            var chunk = text.Substring(r.start, r.end - r.start);
            var cmds = ParseCommandsFromModuleChunk(r.name, chunk);
            if (cmds.Count > 0)
                result.Add(new CommandModule { Name = r.name, Commands = cmds });
        }

        return result;
    }

    private static List<CommandItem> ParseCommandsFromModuleChunk(string moduleName, string chunk)
    {
        var commands = new List<CommandItem>();

        // CMD-XXX-001 — Nombre | Frase: "..."
        var cmdRegex = new Regex(
            @"(?im)^\s*(CMD-[A-Z0-9\-]+)\s*(?:—|-)\s*(.+?)\s*\|\s*Frase\s*:\s*(?:""(.+?)""|(.+))\s*$",
            RegexOptions.Compiled);

        foreach (Match m in cmdRegex.Matches(chunk))
        {
            var id = m.Groups[1].Value.Trim();
            var title = m.Groups[2].Value.Trim();
            var phrase = m.Groups[3].Success ? m.Groups[3].Value.Trim() : m.Groups[4].Value.Trim();
            phrase = phrase.Trim().Trim('"');

            var lowerTitle = title.ToLowerInvariant();
            var requiresConfirmation =
                lowerTitle.Contains("crear") ||
                lowerTitle.Contains("actualizar") ||
                lowerTitle.Contains("eliminar") ||
                lowerTitle.Contains("mover") ||
                lowerTitle.Contains("reprogramar") ||
                lowerTitle.Contains("renombrar") ||
                lowerTitle.Contains("subir") ||
                lowerTitle.Contains("sincron");

            commands.Add(new CommandItem
            {
                Id = id,
                Module = moduleName,
                Title = title,
                Endpoint = "—",
                Function = "—",
                MinParamsLine = "—",
                ExamplesLine = phrase,
                RequiresConfirmation = requiresConfirmation,
                RequiresInternet = true
            });
        }

        return commands;
    }
}

/* ===========================
   MODELOS (MISMO ARCHIVO)
   =========================== */

public class CommandModule
{
    public string Name { get; set; } = "";
    public List<CommandItem> Commands { get; set; } = new();
}

public class CommandItem
{
    public string Id { get; set; } = "";
    public string Module { get; set; } = "";
    public string Title { get; set; } = "";
    public string Endpoint { get; set; } = "—";
    public string Function { get; set; } = "—";
    public string MinParamsLine { get; set; } = "—";
    public string ExamplesLine { get; set; } = "—";

    public bool RequiresConfirmation { get; set; }
    public bool RequiresInternet { get; set; }

    public string IdAndModule => $"{Id} • {Module}";

    public string InternetBadgeText => RequiresInternet ? "Internet" : "Local";
    public string ConfirmBadgeText => RequiresConfirmation ? "Confirmación" : "Sin confirmación";
    public string Description => BuildDescription(Title);

    private static string BuildDescription(string title)
    {
        var t = (title ?? "").ToLowerInvariant();

        if (t.Contains("listar")) return "Muestra una lista de elementos del módulo seleccionado.";
        if (t.Contains("buscar")) return "Permite encontrar elementos por texto o filtros.";
        if (t.Contains("obtener") || t.Contains("abrir") || t.Contains("detalle")) return "Abre el detalle de un elemento específico.";
        if (t.Contains("crear") || t.Contains("agrega") || t.Contains("nuevo")) return "Crea un nuevo registro dentro del sistema.";
        if (t.Contains("actualizar") || t.Contains("cambiar") || t.Contains("marca")) return "Modifica información o estado de un registro existente.";
        if (t.Contains("eliminar") || t.Contains("borrar")) return "Elimina un registro del sistema (acción sensible).";
        if (t.Contains("sincron")) return "Sincroniza información entre servicios (puede tardar).";
        if (t.Contains("delta")) return "Sincroniza únicamente cambios recientes.";
        if (t.Contains("mover") || t.Contains("reprogramar")) return "Reorganiza fechas o prioridades en lote (acción sensible).";
        if (t.Contains("preview")) return "Muestra una vista previa sin modificar información.";
        if (t.Contains("confirmar") || t.Contains("aprobar")) return "Confirma o aprueba un elemento (acción sensible).";
        if (t.Contains("usuarios online") || t.Contains("online")) return "Muestra quién está conectado en este momento.";
        if (t.Contains("mensaje") || t.Contains("chat")) return "Envía un mensaje a otro usuario (acción sensible).";

        return "Ejecuta una acción del asistente dentro del módulo.";
    }

}
