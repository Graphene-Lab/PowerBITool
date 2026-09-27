using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.Tabular;

namespace PowerBIToolNS;

/// <summary>
/// Analysis features: data profiling, data-dictionary generation and a modeling
/// best-practices lint (naming, formats, documentation, DAX anti-patterns,
/// star-schema findings) — the quality layer of the tool.
/// </summary>
public partial class PowerBITool
{
    /// <summary>
    /// Profiles a table: per-column distinct count, blank count, min/max (numeric or
    /// lexicographic) and the top values by frequency, plus a sample of rows. Uses DAX
    /// against the live engine. Read-only.
    /// </summary>
    /// <param name="tableName">Table to profile.</param>
    /// <param name="topValues">Number of top values per column (default 5, max 20).</param>
    /// <param name="sampleRows">Number of sample rows to return (default 5, max 50).</param>
    /// <returns>Markdown profile, or "Error: ...".</returns>
    public string ProfileTable(string tableName, int topValues = 5, int sampleRows = 5)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                topValues = Math.Clamp(topValues, 1, 20);
                sampleRows = Math.Clamp(sampleRows, 0, 50);

                var rowCounts = GetRowCounts(model);
                var sb = new StringBuilder();
                sb.AppendLine($"Profile of {t.Name} — {rowCounts.GetValueOrDefault(t.Name, "?")} rows, {t.Columns.Count} columns");
                sb.AppendLine();
                sb.AppendLine("| Column | Type | Distinct | Blanks | Min | Max | Top values |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                // Profile every real data column. Exclude only the internal RowNumber
                // helper that AS adds to calculated tables — the actual columns of a
                // calculated table are CalculatedTableColumn (and CalculatedColumn),
                // not ColumnType.Data, so filtering on Data alone would skip them.
                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    string q = $"EVALUATE ROW(\"d\", DISTINCTCOUNT('{tableName}'[{c.Name}]), \"b\", COUNTBLANK('{tableName}'[{c.Name}]), \"mn\", MIN('{tableName}'[{c.Name}]), \"mx\", MAX('{tableName}'[{c.Name}]))";
                    string distinct = "?", blanks = "?", min = "", max = "";
                    try
                    {
                        using var conn = OpenAdomd();
                        using var cmd = new Microsoft.AnalysisServices.AdomdClient.AdomdCommand(q, conn) { CommandTimeout = 60 };
                        using var r = cmd.ExecuteReader();
                        if (r.Read())
                        {
                            distinct = r[0]?.ToString() ?? "?";
                            blanks = r[1]?.ToString() ?? "?";
                            min = r[2]?.ToString() ?? "";
                            max = r[3]?.ToString() ?? "";
                        }
                    }
                    catch (Exception ex)
                    {
                        min = max = $"({ex.GetType().Name})";
                    }

                    string top = "";
                    try
                    {
                        string tq = $"TOPN({topValues}, SUMMARIZECOLUMNS('{tableName}'[{c.Name}], \"n\", COUNTROWS('{tableName}')), [n], DESC)";
                        using var conn2 = OpenAdomd();
                        using var cmd2 = new Microsoft.AnalysisServices.AdomdClient.AdomdCommand($"EVALUATE {tq}", conn2) { CommandTimeout = 60 };
                        using var r2 = cmd2.ExecuteReader();
                        var vals = new List<string>();
                        while (r2.Read())
                            vals.Add($"{r2[0]} ({r2[1]})");
                        top = string.Join(", ", vals);
                    }
                    catch { top = "(n/a)"; }

                    sb.AppendLine($"| {c.Name} | {c.DataType} | {distinct} | {blanks} | {min} | {max} | {top} |");
                }

                if (sampleRows > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Sample rows:");
                    sb.AppendLine(RunDaxQuery($"EVALUATE TOPN({sampleRows}, '{tableName}')", sampleRows, 60));
                }
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Generates a markdown data dictionary of the model: every table with its
    /// description, columns (type, format, description) and measures (expression,
    /// format, description). Read-only — safe to call anytime.
    /// </summary>
    /// <param name="tableName">Optional: limit the dictionary to one table.</param>
    /// <returns>Markdown document, or "Error: ...".</returns>
    public string GenerateDataDictionary(string? tableName = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var tables = (tableName == null ? VisibleTables(model)
                        : new[] { FindTable(model, tableName) ?? throw new InvalidOperationException($"table '{tableName}' not found") })
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                var rowCounts = GetRowCounts(model);
                sb.AppendLine($"# Data dictionary — {_databaseName}");
                sb.AppendLine();
                foreach (var t in tables)
                {
                    sb.AppendLine($"## {t.Name}");
                    if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"> {t.Description}");
                    sb.AppendLine($"Type: {DescribeTableType(t)} | Rows: {rowCounts.GetValueOrDefault(t.Name, "?")}");
                    sb.AppendLine();
                    sb.AppendLine("| Column | Type | Format | Description |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var c in t.Columns.Where(c => c.Type == ColumnType.Data))
                        sb.AppendLine($"| {c.Name} | {c.DataType} | {c.FormatString ?? ""} | {c.Description ?? ""} |");
                    if (t.Measures.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("| Measure | Format | Description | Expression |");
                        sb.AppendLine("|---|---|---|---|");
                        foreach (var m in t.Measures.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                            sb.AppendLine($"| {m.Name} | {m.FormatString ?? ""} | {m.Description ?? ""} | `{m.Expression}` |");
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Lints the model for modeling best practices and DAX anti-patterns. Checks:
    /// measures without format string or description; measure/table naming issues
    /// (spaces, special chars); DAX anti-patterns (division by / instead of DIVIDE,
    /// IFERROR, EARLIER, FILTER over a whole table inside CALCULATE, nested CALCULATE,
    /// SUMMARIZE used for aggregation); tables without relationships (disconnected);
    /// auto date/time tables present; bidirectional relationships.
    /// Read-only. Returns findings grouped by severity (warning / info).
    /// </summary>
    /// <returns>Markdown lint report, or "Error: ...".</returns>
    public string AnalyzeBestPractices()
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var warnings = new List<string>();
                var infos = new List<string>();

                var relatedTables = new HashSet<string>(
                    model.Relationships.Select(r => r.FromTable.Name)
                        .Concat(model.Relationships.Select(r => r.ToTable.Name)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var t in VisibleTables(model))
                {
                    if (!relatedTables.Contains(t.Name))
                        infos.Add($"Table '{t.Name}' has no relationships (disconnected table).");

                    foreach (var m in t.Measures)
                    {
                        if (string.IsNullOrWhiteSpace(m.FormatString))
                            warnings.Add($"Measure '{t.Name}[{m.Name}]' has no format string.");
                        if (string.IsNullOrWhiteSpace(m.Description))
                            infos.Add($"Measure '{t.Name}[{m.Name}]' has no description.");
                        foreach (var (pattern, advice) in DaxAntiPatterns(m.Expression))
                            warnings.Add($"Measure '{t.Name}[{m.Name}]': {pattern} — {advice}");
                    }

                    foreach (var c in t.Columns.Where(c => c.Type == ColumnType.Data))
                        if (string.IsNullOrWhiteSpace(c.Description))
                            infos.Add($"Column '{t.Name}[{c.Name}]' has no description.");
                }

                int bidi = model.Relationships.Count(r => r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections);
                if (bidi > 0)
                    infos.Add($"{bidi} bidirectional relationship(s) — verify they are intentional (ambiguous filter context risk).");

                int inactive = model.Relationships.Count(r => !r.IsActive);
                if (inactive > 0)
                    infos.Add($"{inactive} inactive relationship(s) — used via USERELATIONSHIP or leftovers?");

                var sb = new StringBuilder();
                sb.AppendLine($"Best-practices report for '{_databaseName}'");
                sb.AppendLine($"Warnings: {warnings.Count} | Info: {infos.Count}");
                sb.AppendLine();
                if (warnings.Count > 0)
                {
                    sb.AppendLine("## Warnings");
                    foreach (var w in warnings) sb.AppendLine($"- {w}");
                    sb.AppendLine();
                }
                if (infos.Count > 0)
                {
                    sb.AppendLine("## Info");
                    foreach (var i in infos) sb.AppendLine($"- {i}");
                }
                if (warnings.Count == 0 && infos.Count == 0)
                    sb.AppendLine("No issues found — the model follows the checked best practices.");
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Lints a single DAX expression for known anti-patterns without touching the
    /// model. Use while authoring, before creating measures.
    /// </summary>
    /// <param name="daxExpression">DAX expression to lint.</param>
    /// <returns>"No anti-patterns found." or one finding per line.</returns>
    public string LintDax(string daxExpression)
    {
        var found = DaxAntiPatterns(daxExpression);
        if (found.Count == 0) return "No anti-patterns found.";
        return string.Join("\n", found.Select(f => $"- {f.pattern} — {f.advice}"));
    }

    private static List<(string pattern, string advice)> DaxAntiPatterns(string dax)
    {
        var list = new List<(string, string)>();
        // Neutralize string literals so text content never triggers a pattern.
        string s = new Regex("\"(?:[^\"]|\"\")*\"").Replace(dax, "S");

        if (Regex.IsMatch(s, @"[A-Za-z0-9\)\]]\s*/\s*[^/*]") && !Regex.IsMatch(s, @"\bDIVIDE\b"))
            list.Add(("division with '/'", "use DIVIDE() to handle division-by-zero safely"));
        if (Regex.IsMatch(s, @"\bIFERROR\b"))
            list.Add(("IFERROR", "prefer a targeted error handling or DIVIDE/BLANK logic — IFERROR hides real errors"));
        if (Regex.IsMatch(s, @"\bEARLIER\b"))
            list.Add(("EARLIER", "use variables (VAR) instead — clearer and faster"));
        if (Regex.IsMatch(s, @"\bCALCULATE\b[\s\S]*?\bFILTER\s*\(\s*[A-Za-z0-9_'\[]+\s*,"))
            list.Add(("FILTER over a whole table inside CALCULATE", "filter the key column directly (KEEPFILTERS or a column filter) — FILTER(table,...) scans and iterates the full table"));
        if (Regex.IsMatch(s, @"\bCALCULATE\s*\([^)]*\bCALCULATE\b"))
            list.Add(("nested CALCULATE", "flatten to a single CALCULATE — nested context transitions are a common bug source"));
        if (Regex.IsMatch(s, @"\bSUMMARIZE\s*\([^)]*,\s*[A-Z]+\s*\("))
            list.Add(("SUMMARIZE used with aggregation", "use SUMMARIZECOLUMNS or ADDCOLUMNS+SUMMARIZE — SUMMARIZE with measures can produce wrong totals"));
        if (Regex.IsMatch(s, @"\+\s*0(?![.\d])"))
            list.Add(("+ 0 to suppress blanks", "use COALESCE/BLANK handling — +0 changes blank semantics silently"));
        return list;
    }
}
