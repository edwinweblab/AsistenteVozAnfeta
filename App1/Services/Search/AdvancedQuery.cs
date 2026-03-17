using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anfeta.UI.Services.Search
{
    // ====== PLAN (comandos que afectan filtros/orden/paginación) ======
    public sealed record QueryPlan(
        IReadOnlyList<SortSpec> Sorts,
        int? Limit,
        int? Page,
        string? Ext,               // "pdf" o multi "pdf;docx;xlsx" → ya spliteado en ExtList
        IReadOnlyList<string> ExtList,  // NUEVO: lista de extensiones (cuando hay ;)
        bool? OnlyFolders,
        string? FolderContains,
        IReadOnlyList<string> NoPath   // NUEVO: fragmentos de ruta a excluir (nopath:)
    )
    {
        public static readonly QueryPlan Empty =
            new(Array.Empty<SortSpec>(), null, null, null,
                Array.Empty<string>(), null, null, Array.Empty<string>());
    }

    public sealed record SortSpec(string Field, bool Desc);

    // ====== AST (expresión booleana) ======
    public abstract record QNode;
    public sealed record And(QNode L, QNode R) : QNode;
    public sealed record Or(QNode L, QNode R) : QNode;
    public sealed record Not(QNode X) : QNode;
    public sealed record TextTerm(string Pattern) : QNode;
    public sealed record FieldTerm(string Field, FieldOp Op) : QNode;

    /// <summary>
    /// Nodo de expresión regular.
    /// Uso en query:  regex:^00act.*SEO   o   regex:reporte.*(pdf|docx)
    /// Si el patrón es inválido, Compiled = null y evalúa false (no rompe la app).
    /// </summary>
    public sealed record RegexTerm(string RawPattern, Regex? Compiled) : QNode;

    // ====== Operaciones de campo ======
    public abstract record FieldOp;
    public sealed record FieldEq(string Value) : FieldOp;
    public sealed record FieldCmp(string Cmp, string Value) : FieldOp;
    public sealed record FieldRange(string A, string B) : FieldOp;

    // ====== Resultado parseado ======
    public sealed record ParsedQuery(QueryPlan Plan, QNode? Expr);

    public static class AdvancedQueryV3
    {
        // ── Entrada principal ────────────────────────────────────────────
        public static ParsedQuery Parse(string? input)
        {
            var tokens = Lexer.Tokenize(input ?? "");
            var (plan, exprTokens) = ExtractPlanTokens(tokens);
            var expr = Parser.ParseExpression(exprTokens);
            return new ParsedQuery(plan, expr);
        }

        // ── Evaluador ────────────────────────────────────────────────────
        public static bool Evaluate(QNode? node, IItemView it)
        {
            if (node is null) return true;

            return node switch
            {
                And a => Evaluate(a.L, it) && Evaluate(a.R, it),
                Or o => Evaluate(o.L, it) || Evaluate(o.R, it),
                Not n => !Evaluate(n.X, it),
                TextTerm t => MatchText(it.SearchText ?? "", t.Pattern),
                FieldTerm f => MatchField(it, f.Field, f.Op),
                // Regex: evalúa contra SearchText (nombre + ruta)
                RegexTerm rx => rx.Compiled?.IsMatch(it.SearchText ?? "") ?? false,
                _ => true
            };
        }

        // ── Evaluar con Plan (nopath + extList) ──────────────────────────
        /// <summary>
        /// Evaluación completa que también aplica nopath: y la lista de extensiones del Plan.
        /// Úsalo en lugar de Evaluate cuando quieras que el Plan afecte los resultados.
        /// </summary>
        public static bool EvaluateWithPlan(QNode? node, IItemView it, QueryPlan plan)
        {
            // nopath: excluir si la ruta contiene alguno de los fragmentos
            if (plan.NoPath.Count > 0)
            {
                var pathLow = (it.Path ?? "").ToLowerInvariant();
                foreach (var np in plan.NoPath)
                    if (pathLow.Contains(np.ToLowerInvariant()))
                        return false;
            }

            // ext con lista (ext:pdf;docx)
            if (plan.ExtList.Count > 0)
            {
                var ext = (it.Extension ?? "").TrimStart('.').ToLowerInvariant();
                bool extMatch = false;
                foreach (var e in plan.ExtList)
                {
                    if (e == "img")
                    {
                        if (ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" or
                                   "avif" or "heic" or "svg" or "tif" or "tiff" or "ico")
                        { extMatch = true; break; }
                    }
                    else if (ext == e) { extMatch = true; break; }
                }
                if (!extMatch) return false;
            }

            return Evaluate(node, it);
        }

        // ── Texto libre con wildcard * ────────────────────────────────────
        private static bool MatchText(string haystack, string pattern)
            => WildcardMatch(haystack ?? "", pattern ?? "");

        // ── Campos ────────────────────────────────────────────────────────
        private static bool MatchField(IItemView it, string field, FieldOp op)
        {
            field = (field ?? "").Trim().ToLowerInvariant();

            if (field is "name" or "path" or "folder" or "ext" or "type")
            {
                string hay = field switch
                {
                    "name" => it.Name ?? "",
                    "path" => it.Path ?? "",
                    "folder" => it.Folder ?? "",
                    "ext" => (it.Extension ?? "").TrimStart('.'),
                    "type" => it.Type ?? "",
                    _ => ""
                };
                return op switch
                {
                    FieldEq eq => WildcardMatch(hay, eq.Value),
                    FieldCmp cmp when cmp.Cmp is "=" => WildcardMatch(hay, cmp.Value),
                    _ => false
                };
            }

            if (field is "size") return MatchSize(it.SizeBytes, op);
            if (field is "date") return MatchDate(it.ModifiedLocalDate, op);
            if (field is "dm") return MatchDaysModified(it.DaysModified, op);
            if (field is "year") return MatchInt(it.ModifiedLocalDate.Year, op);
            if (field is "month") return MatchInt(it.ModifiedLocalDate.Month, op);

            return false;
        }

        private static bool MatchSize(long sizeBytes, FieldOp op) => op switch
        {
            FieldEq eq => CompareLong(sizeBytes, "=", ParseSize(eq.Value)),
            FieldCmp c => CompareLong(sizeBytes, c.Cmp, ParseSize(c.Value)),
            FieldRange r => sizeBytes >= ParseSize(r.A) && sizeBytes <= ParseSize(r.B),
            _ => false
        };

        private static bool MatchDate(DateTime d, FieldOp op) => op switch
        {
            FieldEq eq => CompareDate(d, "=", ParseDateVal(eq.Value)),
            FieldCmp c => CompareDate(d, c.Cmp, ParseDateVal(c.Value)),
            FieldRange r => d.Date >= ParseDateVal(r.A).Date && d.Date <= ParseDateVal(r.B).Date,
            _ => false
        };

        private static bool MatchDaysModified(int dm, FieldOp op) => op switch
        {
            FieldEq eq => CompareInt(dm, "=", ParseInt(eq.Value)),
            FieldCmp c => CompareInt(dm, c.Cmp, ParseInt(c.Value)),
            FieldRange r => dm >= ParseInt(r.A) && dm <= ParseInt(r.B),
            _ => false
        };

        private static bool MatchInt(int v, FieldOp op) => op switch
        {
            FieldEq eq => CompareInt(v, "=", ParseInt(eq.Value)),
            FieldCmp c => CompareInt(v, c.Cmp, ParseInt(c.Value)),
            FieldRange r => v >= ParseInt(r.A) && v <= ParseInt(r.B),
            _ => false
        };

        // ── Wildcard matcher ──────────────────────────────────────────────
        private static bool WildcardMatch(string haystack, string pattern)
        {
            var h = (haystack ?? "").ToLowerInvariant();
            var p = (pattern ?? "").ToLowerInvariant();

            if (!p.Contains('*')) return h.Contains(p);

            var parts = p.Split('*', StringSplitOptions.None);
            int idx = 0;

            if (!p.StartsWith("*") && parts[0].Length > 0)
            {
                if (!h.StartsWith(parts[0])) return false;
                idx = parts[0].Length;
            }

            for (int i = 1; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (part.Length == 0) continue;
                int found = h.IndexOf(part, idx, StringComparison.Ordinal);
                if (found < 0) return false;
                idx = found + part.Length;
            }

            var last = parts[^1];
            if (!p.EndsWith("*") && last.Length > 0)
                return h.EndsWith(last);

            if (last.Length == 0) return true;
            return h.IndexOf(last, idx, StringComparison.Ordinal) >= 0;
        }

        // ── Parsers de valores ─────────────────────────────────────────────
        private static long ParseSize(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return 0;

            int cut = 0;
            while (cut < s.Length && (char.IsDigit(s[cut]) || s[cut] == '.' || s[cut] == ',')) cut++;

            var numStr = s[..cut].Replace(',', '.');
            var unit = s[cut..].Trim().ToUpperInvariant();

            if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) n = 0;

            long mult = unit switch { "" or "B" => 1, "KB" => 1024L, "MB" => 1024L * 1024, "GB" => 1024L * 1024 * 1024, _ => 1 };
            return (long)(n * mult);
        }

        private static DateTime ParseDateVal(string s)
        {
            s = (s ?? "").Trim();
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) return dt.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)) return dt.Date;
            return DateTime.MinValue;
        }

        private static int ParseInt(string s)
        {
            s = (s ?? "").Trim();
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static bool CompareLong(long a, string cmp, long b) => cmp switch { "=" or "==" => a == b, ">" => a > b, ">=" => a >= b, "<" => a < b, "<=" => a <= b, _ => a == b };
        private static bool CompareInt(int a, string cmp, int b) => cmp switch { "=" or "==" => a == b, ">" => a > b, ">=" => a >= b, "<" => a < b, "<=" => a <= b, _ => a == b };
        private static bool CompareDate(DateTime a, string cmp, DateTime b) { var ad = a.Date; var bd = b.Date; return cmp switch { "=" or "==" => ad == bd, ">" => ad > bd, ">=" => ad >= bd, "<" => ad < bd, "<=" => ad <= bd, _ => ad == bd }; }

        // ── Interface del item ─────────────────────────────────────────────
        public interface IItemView
        {
            string? Name { get; }
            string? Path { get; }
            string? Folder { get; }
            string? Extension { get; }
            string? Type { get; }
            long SizeBytes { get; }
            DateTime ModifiedLocalDate { get; }
            int DaysModified { get; }
            string? SearchText { get; }
        }

        // ════════════════════════════════════════════════════════════════
        //  LEXER
        //  NUEVO: reconoce  |  como OR inline  y  !  como NOT inline
        //  Soporta:
        //    token|token|token  → OR entre variantes
        //    !token             → NOT
        //    .ext               → filtro de extensión
        //    D:\ruta o /ruta    → ruta directa como filtro de folder
        // ════════════════════════════════════════════════════════════════
        private enum TkKind { Word, Phrase, LParen, RParen, And, Or, Not, Minus, End }
        private readonly record struct Tk(TkKind Kind, string Text);

        private static class Lexer
        {
            public static List<Tk> Tokenize(string input)
            {
                var tks = new List<Tk>();
                int i = 0;

                while (i < input.Length)
                {
                    // skip whitespace
                    while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
                    if (i >= input.Length) break;

                    char c = input[i];

                    if (c == '(') { tks.Add(new Tk(TkKind.LParen, "(")); i++; continue; }
                    if (c == ')') { tks.Add(new Tk(TkKind.RParen, ")")); i++; continue; }

                    // '-' como NOT (pero NO si es parte de una ruta como D:\)
                    if (c == '-' && (i == 0 || char.IsWhiteSpace(input[i - 1])))
                    {
                        tks.Add(new Tk(TkKind.Minus, "-"));
                        i++;
                        continue;
                    }

                    // '!' como NOT inline (Everything: !palabra)
                    if (c == '!')
                    {
                        tks.Add(new Tk(TkKind.Not, "NOT"));
                        i++;
                        continue;
                    }

                    // frase entre comillas
                    if (c == '"')
                    {
                        i++;
                        int start = i;
                        while (i < input.Length && input[i] != '"') i++;
                        var phrase = input.Substring(start, i - start);
                        if (i < input.Length && input[i] == '"') i++;
                        tks.Add(new Tk(TkKind.Phrase, phrase));
                        continue;
                    }

                    // ── token (word / field / ruta / pipe-OR) ──────────────
                    int s = i;
                    while (i < input.Length
                           && !char.IsWhiteSpace(input[i])
                           && input[i] != '('
                           && input[i] != ')'
                           && input[i] != '!')   // '!' rompe el token
                        i++;

                    var raw = input.Substring(s, i - s);

                    // AND / OR / NOT explícitos
                    var up = raw.ToUpperInvariant();
                    if (up == "AND") { tks.Add(new Tk(TkKind.And, raw)); continue; }
                    if (up == "OR") { tks.Add(new Tk(TkKind.Or, raw)); continue; }
                    if (up == "NOT") { tks.Add(new Tk(TkKind.Not, raw)); continue; }
                    if (up == "||") { tks.Add(new Tk(TkKind.Or, raw)); continue; }

                    // ── NUEVO: pipe  a|b|c  →  (a OR b OR c) ─────────────
                    if (raw.Contains('|'))
                    {
                        ExpandPipeOr(raw, tks);
                        continue;
                    }

                    // ── NUEVO: .ext al inicio  →  Word normal (lo resuelve ExtractPlan) ─
                    // ya se trata en ExtractPlanTokens como filtro de extensión

                    tks.Add(new Tk(TkKind.Word, raw));
                }

                tks.Add(new Tk(TkKind.End, ""));
                return tks;
            }

            /// <summary>
            /// Convierte "a|b|c" en: ( a OR b OR c )
            /// Los tokens se insertan inline en la lista.
            /// </summary>
            private static void ExpandPipeOr(string raw, List<Tk> tks)
            {
                var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                if (parts.Length == 1)
                {
                    tks.Add(new Tk(TkKind.Word, parts[0]));
                    return;
                }

                // ( part0 OR part1 OR part2 ... )
                tks.Add(new Tk(TkKind.LParen, "("));

                for (int j = 0; j < parts.Length; j++)
                {
                    if (j > 0) tks.Add(new Tk(TkKind.Or, "OR"));
                    tks.Add(new Tk(TkKind.Word, parts[j]));
                }

                tks.Add(new Tk(TkKind.RParen, ")"));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  PARSER (Shunting-yard — sin cambios estructurales)
        // ════════════════════════════════════════════════════════════════
        private static class Parser
        {
            public static QNode? ParseExpression(List<Tk> tokens)
            {
                var normalized = NormalizeMinus(tokens);
                var withAnd = InsertImplicitAnd(normalized);
                var rpn = ToRpn(withAnd);
                return BuildAst(rpn);
            }

            private static List<Tk> NormalizeMinus(List<Tk> tks)
            {
                var outT = new List<Tk>();
                foreach (var tk in tks)
                    outT.Add(tk.Kind == TkKind.Minus ? new Tk(TkKind.Not, "NOT") : tk);
                return outT;
            }

            private static bool IsTerm(TkKind k) => k is TkKind.Word or TkKind.Phrase or TkKind.RParen;
            private static bool StartsTerm(TkKind k) => k is TkKind.Word or TkKind.Phrase or TkKind.LParen or TkKind.Not;

            private static List<Tk> InsertImplicitAnd(List<Tk> tks)
            {
                var outT = new List<Tk>();
                for (int i = 0; i < tks.Count; i++)
                {
                    var a = tks[i];
                    outT.Add(a);
                    if (a.Kind is TkKind.End) break;

                    if (i + 1 < tks.Count)
                    {
                        var b = tks[i + 1];
                        if (IsTerm(a.Kind) && StartsTerm(b.Kind))
                            if (b.Kind is not (TkKind.And or TkKind.Or or TkKind.RParen or TkKind.End))
                                outT.Add(new Tk(TkKind.And, "AND"));
                    }
                }
                return outT;
            }

            private static int Prec(TkKind k) => k switch { TkKind.Not => 3, TkKind.And => 2, TkKind.Or => 1, _ => 0 };
            private static bool IsOp(TkKind k) => k is TkKind.And or TkKind.Or or TkKind.Not;

            private static List<Tk> ToRpn(List<Tk> tks)
            {
                var output = new List<Tk>();
                var ops = new Stack<Tk>();

                foreach (var tk in tks)
                {
                    if (tk.Kind is TkKind.Word or TkKind.Phrase) { output.Add(tk); continue; }
                    if (tk.Kind == TkKind.LParen) { ops.Push(tk); continue; }
                    if (tk.Kind == TkKind.RParen)
                    {
                        while (ops.Count > 0 && ops.Peek().Kind != TkKind.LParen) output.Add(ops.Pop());
                        if (ops.Count > 0 && ops.Peek().Kind == TkKind.LParen) ops.Pop();
                        continue;
                    }
                    if (IsOp(tk.Kind))
                    {
                        while (ops.Count > 0 && IsOp(ops.Peek().Kind) && Prec(ops.Peek().Kind) >= Prec(tk.Kind))
                            output.Add(ops.Pop());
                        ops.Push(tk);
                        continue;
                    }
                    if (tk.Kind == TkKind.End) break;
                }

                while (ops.Count > 0) output.Add(ops.Pop());
                return output;
            }

            private static QNode? BuildAst(List<Tk> rpn)
            {
                var st = new Stack<QNode>();

                foreach (var tk in rpn)
                {
                    if (tk.Kind is TkKind.Word) { st.Push(ParseTerm(tk.Text)); continue; }
                    if (tk.Kind is TkKind.Phrase) { st.Push(new TextTerm(tk.Text)); continue; }

                    if (tk.Kind == TkKind.Not)
                    {
                        if (st.Count >= 1) { var x = st.Pop(); st.Push(new Not(x)); }
                        continue;
                    }
                    if (tk.Kind == TkKind.And)
                    {
                        if (st.Count >= 2) { var r = st.Pop(); var l = st.Pop(); st.Push(new And(l, r)); }
                        continue;
                    }
                    if (tk.Kind == TkKind.Or)
                    {
                        if (st.Count >= 2) { var r = st.Pop(); var l = st.Pop(); st.Push(new Or(l, r)); }
                        continue;
                    }
                }

                return st.Count == 0 ? null : st.Peek();
            }

            private static QNode ParseTerm(string raw)
            {
                // ── regex:patron ──────────────────────────────────────────────
                // Ej: regex:^00act   regex:reporte.*(pdf|docx)   regex:(?i)seo
                if (raw.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                {
                    var pattern = raw["regex:".Length..].Trim();
                    Regex? compiled = null;
                    try
                    {
                        // Timeout de 1s para evitar regex catastrófico
                        compiled = new Regex(
                            pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromSeconds(1));
                    }
                    catch { /* patrón inválido → compiled = null → evalúa false */ }

                    return new RegexTerm(pattern, compiled);
                }

                // ── field:opvalue  |  field:value  |  field:A..B ─────────────
                var idx = raw.IndexOf(':');
                if (idx > 0 && idx < raw.Length - 1)
                {
                    var field = raw[..idx].Trim();
                    var rest = raw[(idx + 1)..].Trim();

                    var dots = rest.IndexOf("..", StringComparison.Ordinal);
                    if (dots >= 0)
                    {
                        var a = rest[..dots].Trim();
                        var b = rest[(dots + 2)..].Trim();
                        return new FieldTerm(field, new FieldRange(a, b));
                    }

                    var (cmp, value) = ReadComparator(rest);
                    return cmp is null
                        ? new FieldTerm(field, new FieldEq(rest))
                        : new FieldTerm(field, new FieldCmp(cmp, value));
                }

                return new TextTerm(raw);
            }

            private static (string? cmp, string value) ReadComparator(string s)
            {
                if (s.StartsWith(">=")) return (">=", s[2..].Trim());
                if (s.StartsWith("<=")) return ("<=", s[2..].Trim());
                if (s.StartsWith("==")) return ("==", s[2..].Trim());
                if (s.StartsWith("=")) return ("=", s[1..].Trim());
                if (s.StartsWith(">")) return (">", s[1..].Trim());
                if (s.StartsWith("<")) return ("<", s[1..].Trim());
                return (null, s);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  EXTRACT PLAN TOKENS
        //  NUEVO: nopath:, ext con ;, .ext, rutas absolutas
        // ════════════════════════════════════════════════════════════════
        private static (QueryPlan plan, List<Tk> exprTokens) ExtractPlanTokens(List<Tk> tokens)
        {
            var sorts = new List<SortSpec>();
            int? limit = null;
            int? page = null;
            string? ext = null;
            var extList = new List<string>();
            bool? onlyFolders = null;
            string? folderContains = null;
            var noPath = new List<string>();

            var expr = new List<Tk>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var tk = tokens[i];

                if (tk.Kind is not TkKind.Word)
                {
                    expr.Add(tk);
                    continue;
                }

                var text = tk.Text ?? "";

                // sort:date:desc
                if (text.StartsWith("sort:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var field = parts[1].Trim().ToLowerInvariant();
                        var ord = parts[2].Trim().ToLowerInvariant();
                        sorts.Add(new SortSpec(field, ord == "desc"));
                    }
                    continue;
                }

                // limit:50
                if (text.StartsWith("limit:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = text["limit:".Length..].Trim();
                    if (int.TryParse(v, out var n) && n > 0) limit = n;
                    continue;
                }

                // page:2
                if (text.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = text["page:".Length..].Trim();
                    if (int.TryParse(v, out var n) && n > 0) page = n;
                    continue;
                }

                // ── ext: con soporte de ; (ext:pdf;docx;xlsx) ────────────
                if (text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = text["ext:".Length..].Trim().TrimStart('.');
                    // separador puede ser ; o ,
                    var parts = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 1)
                    {
                        ext = parts[0].ToLowerInvariant();
                        extList.Add(ext);
                    }
                    else
                    {
                        extList.AddRange(parts.Select(p => p.Trim().TrimStart('.').ToLowerInvariant()));
                        ext = string.Join(";", extList); // guarda la versión raw para compatibilidad
                    }
                    continue;
                }

                // ── NUEVO: .ext al inicio (Everything: .url .pdf .mp4) ────
                if (text.StartsWith(".", StringComparison.Ordinal) && text.Length > 1
                    && !text.Contains(':') && !text.Contains('\\') && !text.Contains('/'))
                {
                    var dotExt = text.TrimStart('.').ToLowerInvariant();
                    // puede contener pipe: .url|.mp4 → ya fue expandido por lexer, pero
                    // si llega sin pipe lo tratamos como filtro de extensión única
                    extList.Add(dotExt);
                    ext ??= dotExt;
                    continue;
                }

                // type:folder | type:file
                if (text.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = text["type:".Length..].Trim().ToLowerInvariant();
                    if (v is "folder" or "folders") onlyFolders = true;
                    else if (v is "file" or "files") onlyFolders = false;
                    continue;
                }

                // folder:Clientes
                if (text.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
                {
                    folderContains = text["folder:".Length..].Trim().Trim('"');
                    continue;
                }

                // ── NUEVO: nopath:fragmento (o nopath:a|b via pipe ya expandido) ──
                if (text.StartsWith("nopath:", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = text["nopath:".Length..].Trim();
                    // si hay pipes (nopath:a|b) el lexer ya los expandió como OR,
                    // pero como nopath llega como un Word sin expandir, lo spliteamos aquí
                    foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = part.Trim();
                        if (!string.IsNullOrWhiteSpace(p)) noPath.Add(p);
                    }
                    continue;
                }

                // ── NUEVO: ruta absoluta como filtro de carpeta ──────────
                // Detecta: D:\..., C:\..., /ruta/linux
                if (IsAbsolutePath(text))
                {
                    // normalizar: reemplaza \ por / para comparar cross-platform
                    folderContains = text.Trim('"').Replace('\\', '/');
                    continue;
                }

                expr.Add(tk);
            }

            var plan = new QueryPlan(sorts, limit, page, ext, extList, onlyFolders, folderContains, noPath);
            return (plan, expr);
        }

        /// <summary>
        /// Detecta si un token parece una ruta absoluta de Windows o Unix.
        /// Ej: D:\Dropbox\..., C:\Users\..., /home/user/...
        /// </summary>
        private static bool IsAbsolutePath(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Windows: letra + :\ o letra + :/
            if (text.Length >= 3 &&
                char.IsLetter(text[0]) &&
                text[1] == ':' &&
                (text[2] == '\\' || text[2] == '/'))
                return true;

            // Unix/Mac: empieza con /
            if (text.StartsWith("/") && text.Length > 1 && !text.StartsWith("//"))
                return true;

            return false;
        }
    }
}