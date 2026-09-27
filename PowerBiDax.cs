using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.AdomdClient;

namespace PowerBIToolNS;

/// <summary>
/// DAX execution over ADOMD.NET against the live Power BI Desktop model: read-only
/// queries with a fail-closed statement guard, row and byte budgets, engine-error
/// redaction, and batch validation of measure expressions without committing them.
/// </summary>
public partial class PowerBITool
{
    /// <summary>Max characters of a result cell before truncation.</summary>
    private const int MaxCellChars = 200;
    /// <summary>Hard byte budget for a returned result set (UTF-8), on top of maxRows.</summary>
    private const int MaxResultBytes = 64 * 1024;
    /// <summary>Default DAX command timeout (seconds).</summary>
    private const int DefaultDaxTimeout = 120;

    /// <summary>
    /// Executes a read-only DAX query against the live model and returns the result as a
    /// markdown table. Only EVALUATE queries (optionally with DEFINE ... measures/variables)
    /// and SELECT ... $SYSTEM.* DMV rowsets are admitted — anything that could modify the
    /// model is rejected before it reaches the engine. Results are capped by maxRows and a
    /// byte budget; truncation is reported explicitly (never silently).
    /// </summary>
    /// <param name="daxQuery">A DAX query starting with EVALUATE (or DEFINE ... EVALUATE), or a DMV SELECT against $SYSTEM.*.</param>
    /// <param name="maxRows">Maximum rows to return (default 100).</param>
    /// <param name="timeoutSeconds">Query timeout in seconds (default 120).</param>
    /// <returns>Markdown table with a row-count footer, or "Error: ..." (guard rejection, DAX error with the engine message redacted of the query echo).</returns>
    public string RunDaxQuery(string daxQuery, int maxRows = 100, int timeoutSeconds = DefaultDaxTimeout)
    {
        try
        {
            lock (ConnLock)
            {
                RequireDb();
                var guard = DaxGuard.Check(daxQuery);
                if (!guard.Allowed)
                    return $"Error: query rejected by the read-only guard — {guard.Reason}";
                maxRows = Math.Clamp(maxRows, 1, 10000);

                using var conn = OpenAdomd();
                using var cmd = new AdomdCommand(daxQuery, conn) { CommandTimeout = timeoutSeconds };
                using var reader = cmd.ExecuteReader();
                return FormatResult(reader, maxRows);
            }
        }
        catch (Exception ex) { return $"Error: {RedactEngineError(ex.Message, daxQuery)}"; }
    }

    /// <summary>
    /// Validates a DAX expression (scalar or table) without adding it to the model:
    /// the expression is evaluated as a temporary measure via DEFINE MEASURE ... EVALUATE.
    /// Use before CreateMeasure/UpdateMeasure to catch syntax and evaluation errors early.
    /// </summary>
    /// <param name="tableName">Table context for the temporary measure (its columns are visible to the expression).</param>
    /// <param name="daxExpression">The measure expression to validate (no table prefix).</param>
    /// <param name="timeoutSeconds">Query timeout in seconds (default 60).</param>
    /// <returns>"Valid: expression evaluates without errors." or "Invalid: ..." with the engine error.</returns>
    public string ValidateDax(string tableName, string daxExpression, int timeoutSeconds = 60)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                // Validate in row context: the expression is evaluated per row via
                // SELECTCOLUMNS, so both scalar/measure-style and calculated-column-style
                // (row-context) expressions are accepted, while syntax and per-row
                // evaluation errors surface. TOPN(1) keeps the probe cheap.
                string probe =
                    $"EVALUATE TOPN(1, SELECTCOLUMNS('{tableName}', \"__pbitool_validate_\", {daxExpression}))";
                var guard = DaxGuard.Check(probe);
                if (!guard.Allowed)
                    return $"Error: expression rejected by the read-only guard — {guard.Reason}";

                using var conn = OpenAdomd();
                using var cmd = new AdomdCommand(probe, conn) { CommandTimeout = timeoutSeconds };
                using var reader = cmd.ExecuteReader();
                reader.Read();
                string sample = reader.FieldCount > 0 ? reader[0]?.ToString() ?? "(blank)" : "(no value)";
                return $"Valid: expression evaluates without errors. Sample value: {sample}";
            }
        }
        catch (Exception ex) { return $"Invalid: {RedactEngineError(ex.Message, daxExpression)}"; }
    }

    /// <summary>
    /// Batch-validates several measure expressions in one round trip: all expressions are
    /// defined as temporary measures and evaluated together; if the batch fails, each
    /// expression is validated individually to isolate the offenders. Reports per
    /// expression. Use before creating many measures at once.
    /// </summary>
    /// <param name="measuresJson">JSON array of objects: [{"table":"T","name":"M","expression":"..."}].</param>
    /// <param name="timeoutSeconds">Query timeout in seconds per validation (default 60).</param>
    /// <returns>Per-expression "name: OK" or "name: ERROR ..." lines.</returns>
    public string ValidateMeasures(string measuresJson, int timeoutSeconds = 60)
    {
        try
        {
            List<MeasureSpec>? specs;
            try
            {
                specs = System.Text.Json.JsonSerializer.Deserialize<List<MeasureSpec>>(measuresJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex) { return $"Error: measuresJson is not a valid JSON array — {ex.Message}"; }
            if (specs == null || specs.Count == 0) return "Error: measuresJson is empty.";

            lock (ConnLock)
            {
                var model = RequireDb().Model;
                foreach (var s in specs)
                {
                    if (string.IsNullOrWhiteSpace(s.Table) || string.IsNullOrWhiteSpace(s.Expression))
                        return $"Error: every entry needs table and expression. Bad entry: {s.Name ?? s.Table}";
                    if (FindTable(model, s.Table) == null)
                        return $"Error: table '{s.Table}' not found.";
                }

                // Batch attempt: define all measures, evaluate them in one ROW().
                var define = new StringBuilder("DEFINE ");
                var rowArgs = new StringBuilder();
                for (int i = 0; i < specs.Count; i++)
                {
                    var s = specs[i];
                    define.Append($"MEASURE '{s.Table}'[__v{i}] = {s.Expression} ");
                    if (i > 0) rowArgs.Append(", ");
                    rowArgs.Append($"\"m{i}\", CALCULATE('{s.Table}'[__v{i}])");
                }
                string batch = define.ToString() + "EVALUATE ROW(" + rowArgs.ToString() + ")";

                try
                {
                    using var conn = OpenAdomd();
                    using var cmd = new AdomdCommand(batch, conn) { CommandTimeout = timeoutSeconds };
                    using var reader = cmd.ExecuteReader();
                    reader.Read();
                    var sb = new StringBuilder();
                    for (int i = 0; i < specs.Count; i++)
                        sb.AppendLine($"{specs[i].Name ?? specs[i].Table + "[__v" + i + "]"}: OK (value: {reader[i]})");
                    return sb.ToString().TrimEnd();
                }
                catch (Exception batchEx)
                {
                    // Isolate: validate one by one.
                    var sb = new StringBuilder();
                    sb.AppendLine($"Batch failed ({RedactEngineError(batchEx.Message, batch)}). Per-expression results:");
                    foreach (var s in specs)
                    {
                        string r = ValidateDax(s.Table, s.Expression, timeoutSeconds);
                        sb.AppendLine($"{s.Name ?? s.Table}: {(r.StartsWith("Valid") ? "OK" : r)}");
                    }
                    return sb.ToString().TrimEnd();
                }
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private sealed class MeasureSpec
    {
        public string Table { get; set; } = "";
        public string? Name { get; set; }
        public string Expression { get; set; } = "";
    }

    private static AdomdConnection OpenAdomd()
    {
        var conn = new AdomdConnection($"Data Source=localhost:{_port};Connect Timeout=10");
        conn.Open();
        return conn;
    }

    /// <summary>Runs a DAX query expected to return a single scalar and reads it as Int64.</summary>
    private long ScalarLong(string dax)
    {
        using var conn = OpenAdomd();
        using var cmd = new AdomdCommand(dax, conn) { CommandTimeout = 60 };
        using var r = cmd.ExecuteReader();
        return r.Read() ? Convert.ToInt64(r[0]) : 0L;
    }

    /// <summary>Formats a reader into a markdown table with row/byte budgets.</summary>
    private static string FormatResult(IDataReader reader, int maxRows)
    {
        var sb = new StringBuilder();
        var headers = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++) headers[i] = reader.GetName(i);
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Join("|", headers.Select(_ => "---")) + "|");

        int rows = 0;
        int bytesWritten = 0;
        bool truncatedByRows = false, truncatedByBytes = false;
        while (reader.Read())
        {
            if (rows >= maxRows) { truncatedByRows = true; break; }
            var line = new StringBuilder("| ");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
                if (val.Length > MaxCellChars) val = val[..MaxCellChars] + "…";
                line.Append(val.Replace("|", "\\|").Replace("\n", " ").Replace("\r", ""));
                if (i < reader.FieldCount - 1) line.Append(" | ");
            }
            line.Append(" |");
            bytesWritten += Encoding.UTF8.GetByteCount(line.ToString());
            if (bytesWritten > MaxResultBytes)
            { truncatedByBytes = true; break; }
            sb.AppendLine(line.ToString());
            rows++;
        }

        if (rows == 0) return "Query returned 0 rows.";
        var footer = $"\n{rows} row(s) returned.";
        if (truncatedByRows) footer += $" TRUNCATED at maxRows={maxRows} — do not sum/count this result; raise maxRows or aggregate in DAX.";
        if (truncatedByBytes) footer += " TRUNCATED by byte budget — aggregate in DAX instead of exporting raw rows.";
        return sb.ToString() + footer;
    }

    /// <summary>The AS engine embeds the full query text in its exception messages;
    /// strip it so business data in queries never leaks into logs or LLM context.</summary>
    internal static string RedactEngineError(string message, string query)
    {
        if (string.IsNullOrEmpty(query)) return message;
        var q = query.Trim();
        // The engine echoes the query verbatim or with trailing whitespace variants.
        message = message.Replace(q, "<query>");
        // Also redact the first line of multi-line queries (common echo form).
        var firstLine = q.Split('\n')[0].Trim();
        if (firstLine.Length > 20) message = message.Replace(firstLine, "<query>");
        return message;
    }
}

/// <summary>
/// Fail-closed read-only guard for DAX statements: neutralizes comments, string
/// literals and bracketed/quoted identifiers (so their content can never smuggle a
/// token), then admits ONLY: EVALUATE queries, DEFINE...EVALUATE queries, and
/// SELECT ... FROM $SYSTEM.* DMV rowsets. Everything else — DDL, XMLA, mixed
/// statements, multiple statements, unknown leading tokens — is rejected.
/// </summary>
internal static class DaxGuard
{
    internal sealed record Verdict(bool Allowed, string Reason);

    private static readonly Regex BlockComment = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex StringLit = new(@"""(?:[^""]|"""")*""", RegexOptions.Compiled);
    private static readonly Regex BracketIdent = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex SingleQuoted = new(@"'[^']*'", RegexOptions.Compiled);
    private static readonly Regex DdlToken = new(
        @"\b(CREATE|ALTER|DROP|INSERT|UPDATE|DELETE|GRANT|REVOKE|SAVE|RESTORE|PROCESS|COMMIT|ROLLBACK|TRANSACTION|BATCH|BACKUP)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // An XML tag opens with '<' followed by a name char, '/', '?' or '!'. DAX
    // comparison operators (<, >, <=, >=, <>) are legal and must NOT be caught here.
    private static readonly Regex XmlTag = new(@"<[A-Za-z/!?]", RegexOptions.Compiled);

    internal static Verdict Check(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new Verdict(false, "empty query");

        // Neutralize everything that can hide tokens.
        string s = BlockComment.Replace(query, " ");
        s = LineComment.Replace(s, " ");
        s = StringLit.Replace(s, "S");
        s = BracketIdent.Replace(s, "B");
        s = SingleQuoted.Replace(s, "Q");

        // No statement separators after neutralization (one statement only).
        if (s.Contains(';') && !s.TrimEnd().EndsWith(';'))
            return new Verdict(false, "multiple statements are not allowed");
        if (s.TrimEnd().EndsWith(';') && s[..s.TrimEnd(';').Length].Contains(';'))
            return new Verdict(false, "multiple statements are not allowed");

        // No XMLA/XML tag markup. Comparison operators (<, >, <=, >=, <>) are legal
        // DAX and must not be mistaken for markup: only a '<' that opens a tag is rejected.
        if (XmlTag.IsMatch(s))
            return new Verdict(false, "XMLA/markup is not allowed");

        // DDL and write tokens are never admitted.
        var ddl = DdlToken.Match(s);
        if (ddl.Success)
            return new Verdict(false, $"write/DDL keyword '{ddl.Value.ToUpperInvariant()}' is not allowed");

        var tokens = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new Verdict(false, "no tokens");

        string first = tokens[0].ToUpperInvariant();
        if (first == "EVALUATE")
            return new Verdict(true, "read-only DAX query");

        if (first == "DEFINE")
        {
            // DEFINE ... EVALUATE: the EVALUATE must be present and no second DEFINE.
            int evalCount = tokens.Count(t => t.Equals("EVALUATE", StringComparison.OrdinalIgnoreCase));
            int defineCount = tokens.Count(t => t.Equals("DEFINE", StringComparison.OrdinalIgnoreCase));
            if (evalCount == 1 && defineCount == 1)
                return new Verdict(true, "read-only DEFINE...EVALUATE query");
            return new Verdict(false, "malformed DEFINE...EVALUATE query");
        }

        if (first == "SELECT")
        {
            // DMV only: must reference $SYSTEM.
            if (query.IndexOf("$SYSTEM.", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Verdict(true, "DMV rowset");
            return new Verdict(false, "SELECT is admitted only against $SYSTEM.* DMVs");
        }

        return new Verdict(false, $"statement must start with EVALUATE, DEFINE...EVALUATE or SELECT $SYSTEM.* (got '{first}')");
    }
}
