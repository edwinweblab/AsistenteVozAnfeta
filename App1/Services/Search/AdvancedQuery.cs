using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace Anfeta.UI.Services.Search
{
    // ====== PLAN (comandos que afectan filtros/orden/paginación) ======
    public sealed record QueryPlan(
        IReadOnlyList<SortSpec> Sorts,
        int? Limit,
        int? Page,

        // NUEVO:
        string? Ext,          // "pdf" o "img"
        bool? OnlyFolders,    // true/false si el usuario lo pidió
        string? FolderContains // texto a buscar en carpeta/ruta
    )
    {
        public static readonly QueryPlan Empty =
            new(Array.Empty<SortSpec>(), null, null, null, null, null);
    }


    public sealed record SortSpec(string Field, bool Desc);

    // ====== AST (expresión booleana) ======
    public abstract record QNode;
    public sealed record And(QNode L, QNode R) : QNode;
    public sealed record Or(QNode L, QNode R) : QNode;
    public sealed record Not(QNode X) : QNode;

    // Termino: texto libre o campo:valor/comp/rango
    public sealed record TextTerm(string Pattern) : QNode; // puede incluir * y/o comillas ya resueltas
    public sealed record FieldTerm(string Field, FieldOp Op) : QNode;

    // ====== Operaciones de campo ======
    public abstract record FieldOp;

    public sealed record FieldEq(string Value) : FieldOp;                // field:value
    public sealed record FieldCmp(string Cmp, string Value) : FieldOp;   // field:>10MB, field:<=3
    public sealed record FieldRange(string A, string B) : FieldOp;       // field:A..B

    // ====== Resultado parseado ======
    public sealed record ParsedQuery(QueryPlan Plan, QNode? Expr);

    public static class AdvancedQueryV3
    {
        // Entrada principal: devuelve plan + expresión (puede ser null si solo hubo comandos)
        public static ParsedQuery Parse(string? input)
        {
            var tokens = Lexer.Tokenize(input ?? "");
            var (plan, exprTokens) = ExtractPlanTokens(tokens);
            var expr = Parser.ParseExpression(exprTokens);
            return new ParsedQuery(plan, expr);
        }

        // ====== Evaluador: convierte Expr a predicate ======
        // Tú lo conectas contra tu item real con los selectores.
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

                _ => true
            };
        }

        // ---- Texto libre con wildcard * (simple y estable) ----
        private static bool MatchText(string haystack, string pattern)
        {
            haystack ??= "";
            pattern ??= "";

            return WildcardMatch(haystack, pattern);
        }

        // ---- Campos mínimos útiles para tu app local ----
        // Puedes ampliar después sin romper la gramática
        private static bool MatchField(IItemView it, string field, FieldOp op)
        {
            field = (field ?? "").Trim().ToLowerInvariant();

            // Campos de texto
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

            // Campos numéricos/fecha (size/date/dm/year/month)
            if (field is "size")
                return MatchSize(it.SizeBytes, op);

            if (field is "date")
                return MatchDate(it.ModifiedLocalDate, op);

            if (field is "dm")
                return MatchDaysModified(it.DaysModified, op);

            if (field is "year")
                return MatchInt(it.ModifiedLocalDate.Year, op);

            if (field is "month")
                return MatchInt(it.ModifiedLocalDate.Month, op);

            return false;
        }

        private static bool MatchSize(long sizeBytes, FieldOp op)
        {
            // size:>10MB, size:1MB..5MB
            return op switch
            {
                FieldEq eq => CompareLong(sizeBytes, "=", ParseSize(eq.Value)),
                FieldCmp c => CompareLong(sizeBytes, c.Cmp, ParseSize(c.Value)),
                FieldRange r =>
                    sizeBytes >= ParseSize(r.A) && sizeBytes <= ParseSize(r.B),
                _ => false
            };
        }

        private static bool MatchDate(DateTime dateLocal, FieldOp op)
        {
            // date:<2023-01-01  o rango date:2023-01-01..2023-12-31
            return op switch
            {
                FieldEq eq => CompareDate(dateLocal, "=", ParseDate(eq.Value)),
                FieldCmp c => CompareDate(dateLocal, c.Cmp, ParseDate(c.Value)),
                FieldRange r =>
                    dateLocal.Date >= ParseDate(r.A).Date && dateLocal.Date <= ParseDate(r.B).Date,
                _ => false
            };
        }

        private static bool MatchDaysModified(int dm, FieldOp op)
        {
            return op switch
            {
                FieldEq eq => CompareInt(dm, "=", ParseInt(eq.Value)),
                FieldCmp c => CompareInt(dm, c.Cmp, ParseInt(c.Value)),
                FieldRange r =>
                    dm >= ParseInt(r.A) && dm <= ParseInt(r.B),
                _ => false
            };
        }

        private static bool MatchInt(int v, FieldOp op)
        {
            return op switch
            {
                FieldEq eq => CompareInt(v, "=", ParseInt(eq.Value)),
                FieldCmp c => CompareInt(v, c.Cmp, ParseInt(c.Value)),
                FieldRange r => v >= ParseInt(r.A) && v <= ParseInt(r.B),
                _ => false
            };
        }

        // ====== Wildcard matcher: soporta * (contiene / empieza / termina) ======
        // Ej: inf*  *final*  doc*
        private static bool WildcardMatch(string haystack, string pattern)
        {
            haystack ??= "";
            pattern ??= "";
            var h = haystack;
            var p = pattern;

            // Case-insensitive estable
            h = h.ToLowerInvariant();
            p = p.ToLowerInvariant();

            if (!p.Contains('*'))
                return h.Contains(p);

            // Split por *
            var parts = p.Split('*', StringSplitOptions.None);
            int idx = 0;

            // si no empieza con *, la primera parte debe estar al inicio
            if (!p.StartsWith("*") && parts[0].Length > 0)
            {
                if (!h.StartsWith(parts[0])) return false;
                idx = parts[0].Length;
            }

            // partes intermedias en orden
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (part.Length == 0) continue;

                int found = h.IndexOf(part, idx, StringComparison.Ordinal);
                if (found < 0) return false;
                idx = found + part.Length;
            }

            // si no termina con *, la última parte debe estar al final
            var last = parts[^1];
            if (!p.EndsWith("*") && last.Length > 0)
                return h.EndsWith(last);

            // si termina con * o last vacío, ya está
            if (last.Length == 0) return true;

            // si termina con *, basta con que aparezca después de idx
            return h.IndexOf(last, idx, StringComparison.Ordinal) >= 0;
        }

        // ====== Parsers de valores ======
        private static long ParseSize(string s)
        {
            // 10MB, 5kb, 1GB, 1234
            s = (s ?? "").Trim();
            if (s.Length == 0) return 0;

            // separar número + unidad
            int cut = 0;
            while (cut < s.Length && (char.IsDigit(s[cut]) || s[cut] == '.' || s[cut] == ',')) cut++;

            var numStr = s[..cut].Replace(',', '.');
            var unit = s[cut..].Trim().ToUpperInvariant();

            if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                n = 0;

            long mult = unit switch
            {
                "" => 1,
                "B" => 1,
                "KB" => 1024L,
                "MB" => 1024L * 1024,
                "GB" => 1024L * 1024 * 1024,
                _ => 1
            };

            return (long)(n * mult);
        }

        private static DateTime ParseDate(string s)
        {
            // yyyy-MM-dd
            s = (s ?? "").Trim();
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
                return dt.Date;

            // fallback: parse normal
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                return dt.Date;

            return DateTime.MinValue;
        }

        private static int ParseInt(string s)
        {
            s = (s ?? "").Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            return 0;
        }

        private static bool CompareLong(long a, string cmp, long b) => cmp switch
        {
            "=" or "==" => a == b,
            ">" => a > b,
            ">=" => a >= b,
            "<" => a < b,
            "<=" => a <= b,
            _ => a == b
        };

        private static bool CompareInt(int a, string cmp, int b) => cmp switch
        {
            "=" or "==" => a == b,
            ">" => a > b,
            ">=" => a >= b,
            "<" => a < b,
            "<=" => a <= b,
            _ => a == b
        };

        private static bool CompareDate(DateTime a, string cmp, DateTime b)
        {
            var ad = a.Date; var bd = b.Date;
            return cmp switch
            {
                "=" or "==" => ad == bd,
                ">" => ad > bd,
                ">=" => ad >= bd,
                "<" => ad < bd,
                "<=" => ad <= bd,
                _ => ad == bd
            };
        }

        // ====== Interfaces mínimas para evaluar items sin tocar tu modelo ======
        // Esto lo adaptas con un wrapper en SearchView sin cambiar SearchResultRow
        public interface IItemView
        {
            string? Name { get; }
            string? Path { get; }      // ruta completa
            string? Folder { get; }    // carpeta/dir
            string? Extension { get; } // "pdf" o ".pdf"
            string? Type { get; }      // "document", "image", etc (si lo tienes)
            long SizeBytes { get; }
            DateTime ModifiedLocalDate { get; }
            int DaysModified { get; }
            string? SearchText { get; } // normalmente Name + Path
        }

        // ====== LEXER + PARSER (Shunting-yard) ======
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
                    while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
                    if (i >= input.Length) break;

                    char c = input[i];

                    if (c == '(') { tks.Add(new Tk(TkKind.LParen, "(")); i++; continue; }
                    if (c == ')') { tks.Add(new Tk(TkKind.RParen, ")")); i++; continue; }

                    if (c == '-') { tks.Add(new Tk(TkKind.Minus, "-")); i++; continue; }

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

                    // word/operator/field
                    int s = i;
                    while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '(' && input[i] != ')')
                        i++;
                    var raw = input.Substring(s, i - s);

                    var up = raw.ToUpperInvariant();
                    if (up == "AND") tks.Add(new Tk(TkKind.And, raw));
                    else if (up == "OR") tks.Add(new Tk(TkKind.Or, raw));
                    else if (up == "NOT") tks.Add(new Tk(TkKind.Not, raw));
                    else tks.Add(new Tk(TkKind.Word, raw));
                }

                tks.Add(new Tk(TkKind.End, ""));
                return tks;
            }
        }

        private static class Parser
        {
            public static QNode? ParseExpression(List<Tk> tokens)
            {
                // Convertimos "-" a NOT (unario) para que funcione "-borrador"
                var normalized = NormalizeMinus(tokens);

                // Insertar AND implícito entre términos adyacentes (como Notion/Everything)
                var withAnd = InsertImplicitAnd(normalized);

                // Shunting-yard a RPN con precedencia: NOT > AND > OR
                var rpn = ToRpn(withAnd);

                // Construir AST desde RPN
                return BuildAst(rpn);
            }

            private static List<Tk> NormalizeMinus(List<Tk> tks)
            {
                var outT = new List<Tk>();
                for (int i = 0; i < tks.Count; i++)
                {
                    var tk = tks[i];
                    if (tk.Kind == TkKind.Minus)
                        outT.Add(new Tk(TkKind.Not, "NOT"));
                    else
                        outT.Add(tk);
                }
                return outT;
            }

            private static bool IsTerm(TkKind k) =>
                k is TkKind.Word or TkKind.Phrase or TkKind.RParen;

            private static bool StartsTerm(TkKind k) =>
                k is TkKind.Word or TkKind.Phrase or TkKind.LParen or TkKind.Not;

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
                        // (term) (term)  => AND implícito
                        if (IsTerm(a.Kind) && StartsTerm(b.Kind))
                        {
                            // No meter AND si ya hay operador explícito
                            if (b.Kind is not (TkKind.And or TkKind.Or or TkKind.RParen or TkKind.End))
                                outT.Add(new Tk(TkKind.And, "AND"));
                        }
                    }
                }
                return outT;
            }

            private static int Prec(TkKind k) => k switch
            {
                TkKind.Not => 3,
                TkKind.And => 2,
                TkKind.Or => 1,
                _ => 0
            };

            private static bool IsOp(TkKind k) => k is TkKind.And or TkKind.Or or TkKind.Not;

            private static List<Tk> ToRpn(List<Tk> tks)
            {
                var output = new List<Tk>();
                var ops = new Stack<Tk>();

                for (int i = 0; i < tks.Count; i++)
                {
                    var tk = tks[i];

                    if (tk.Kind is TkKind.Word or TkKind.Phrase)
                    {
                        output.Add(tk);
                        continue;
                    }

                    if (tk.Kind == TkKind.LParen) { ops.Push(tk); continue; }
                    if (tk.Kind == TkKind.RParen)
                    {
                        while (ops.Count > 0 && ops.Peek().Kind != TkKind.LParen)
                            output.Add(ops.Pop());
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
                    if (tk.Kind is TkKind.Word)
                    {
                        st.Push(ParseTerm(tk.Text));
                    }
                    else if (tk.Kind is TkKind.Phrase)
                    {
                        st.Push(new TextTerm(tk.Text));
                    }
                    else if (tk.Kind == TkKind.Not)
                    {
                        // ✅ Tolerante: si no hay nada que negar, lo ignoramos
                        if (st.Count >= 1)
                        {
                            var x = st.Pop();
                            st.Push(new Not(x));
                        }
                        // else: ignore
                    }
                    else if (tk.Kind == TkKind.And)
                    {
                        // ✅ Tolerante: si faltan operandos, ignora
                        if (st.Count >= 2)
                        {
                            var r = st.Pop();
                            var l = st.Pop();
                            st.Push(new And(l, r));
                        }
                    }
                    else if (tk.Kind == TkKind.Or)
                    {
                        // ✅ Tolerante: si faltan operandos, ignora
                        if (st.Count >= 2)
                        {
                            var r = st.Pop();
                            var l = st.Pop();
                            st.Push(new Or(l, r));
                        }
                    }
                }

                return st.Count == 0 ? null : st.Peek();
            }


            private static QNode ParseTerm(string raw)
            {
                // field:opvalue  | field:value | field:A..B
                // Ej: size:>10MB, date:<2023-01-01, dm:=7, year:>=2022, month:3..6
                var idx = raw.IndexOf(':');
                if (idx > 0 && idx < raw.Length - 1)
                {
                    var field = raw[..idx].Trim();
                    var rest = raw[(idx + 1)..].Trim();

                    // rango A..B
                    var dots = rest.IndexOf("..", StringComparison.Ordinal);
                    if (dots >= 0)
                    {
                        var a = rest[..dots].Trim();
                        var b = rest[(dots + 2)..].Trim();
                        return new FieldTerm(field, new FieldRange(a, b));
                    }

                    // comparadores al inicio
                    var (cmp, value) = ReadComparator(rest);
                    if (cmp is null)
                        return new FieldTerm(field, new FieldEq(rest));
                    else
                        return new FieldTerm(field, new FieldCmp(cmp, value));
                }

                return new TextTerm(raw);
            }

            private static (string? cmp, string value) ReadComparator(string s)
            {
                // >=, <=, ==, =, >, <
                if (s.StartsWith(">=")) return (">=", s[2..].Trim());
                if (s.StartsWith("<=")) return ("<=", s[2..].Trim());
                if (s.StartsWith("==")) return ("==", s[2..].Trim());
                if (s.StartsWith("=")) return ("=", s[1..].Trim());
                if (s.StartsWith(">")) return (">", s[1..].Trim());
                if (s.StartsWith("<")) return ("<", s[1..].Trim());

                return (null, s);
            }
        }

        // ====== Extraer plan: sort/limit/page del stream ======
        private static (QueryPlan plan, List<Tk> exprTokens) ExtractPlanTokens(List<Tk> tokens)
        {
            var sorts = new List<SortSpec>();
            int? limit = null;
            int? page = null;

            // ✅ NUEVO: filtros parseados desde query (sin tocar UI)
            string? ext = null;
            bool? onlyFolders = null;
            string? folderContains = null;

            var expr = new List<Tk>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var tk = tokens[i];

                // dejamos pasar operadores/paréntesis tal cual
                if (tk.Kind is not TkKind.Word)
                {
                    expr.Add(tk);
                    continue;
                }

                var text = tk.Text ?? "";

                // sort:date:desc | sort:name:asc
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

                // ✅ ext:pdf | ext:img | ext:.pdf
                if (text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                {
                    ext = text["ext:".Length..].Trim().TrimStart('.').ToLowerInvariant();
                    continue;
                }

                // ✅ type:folder | type:file
                if (text.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = text["type:".Length..].Trim().ToLowerInvariant();
                    if (v is "folder" or "folders") onlyFolders = true;
                    else if (v is "file" or "files") onlyFolders = false;
                    continue;
                }

                // ✅ folder:Clientes (por ahora sin comillas complejas)
                if (text.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
                {
                    folderContains = text["folder:".Length..].Trim().Trim('"');
                    continue;
                }

                // si no fue comando, sí entra al AST
                expr.Add(tk);
            }

            var plan = new QueryPlan(sorts, limit, page, ext, onlyFolders, folderContains);
            return (plan, expr);
        }
    }
}
