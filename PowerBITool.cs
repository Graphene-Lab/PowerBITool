using System.Diagnostics;
using System.Text;
using AIOrchestrator;
using AIOrchestrator.API;
using Microsoft.AnalysisServices.Tabular;
using Server = Microsoft.AnalysisServices.Tabular.Server;
using Database = Microsoft.AnalysisServices.Tabular.Database;

namespace PowerBIToolNS;

/// <summary>
/// Works with the locally installed Microsoft Power BI Desktop (no cloud, no Fabric):
/// connects to the local Analysis Services instance (msmdsrv.exe) that Power BI Desktop
/// spawns for each open .pbix, then inspects and edits the live semantic model — tables,
/// columns, measures, relationships — runs and validates DAX queries, profiles data,
/// generates data dictionaries and lints modeling best practices.
///
/// CONNECT FIRST: every method except ConnectPowerBiDesktop and ListOpenReports requires
/// an active connection. Power BI Desktop must be running with a .pbix report open.
///
/// PERSISTENCE: modeling changes are applied to the LIVE in-memory model and become
/// visible in Power BI Desktop immediately (that is the visual feedback loop), but they
/// are NOT written to the .pbix file — the user must press Ctrl+S in Power BI Desktop
/// to persist them. Every mutating result carries this note.
///
/// SCOPE: only the semantic model is reachable through the local endpoint. Report pages
/// and visuals (the report canvas) cannot be read or changed over this connection.
/// </summary>
[ToolPurpose(ToolPurpose.BusinessIntelligence)]
public partial class PowerBITool : BaseAgentTool
{
    public override bool SupportsAsyncTasks => true;

    /// <summary>Appended to every mutating result: the change is live but ephemeral
    /// until the user saves in Power BI Desktop.</summary>
    internal const string LiveNote =
        " Applied to the LIVE model in Power BI Desktop (visible now). Not saved to the .pbix file — the user must press Ctrl+S in Power BI Desktop to persist it.";

    // The agent may create several tool instances per conversation; the live AMO
    // connection is process-wide so a connect once serves every later call.
    private static readonly object ConnLock = new();
    private static Server? _server;
    private static Database? _db;
    private static int _port;
    private static int _enginePid;
    private static int _desktopPid;
    private static string? _databaseName;
    private static string? _fingerprint;

    /// <summary>
    /// Connects to the local Analysis Services instance of a running Power BI Desktop.
    /// Discovery: finds PBIDesktop.exe processes, their msmdsrv.exe children and the
    /// dynamic localhost port each one listens on, then reads the live model name.
    /// With one Power BI Desktop report open, omit pbixFileName. With several reports
    /// open, pass the .pbix file name (with or without extension) of the target report;
    /// call ListOpenReports first to see what is open. Reconnecting to the same report
    /// is a no-op; connecting to a different one switches the live target.
    /// </summary>
    /// <param name="pbixFileName">Name of the open report to target (e.g. "SalesReport" or "SalesReport.pbix"). Omit when only one report is open.</param>
    /// <returns>Connected confirmation with model name and port, "MultipleReports: ..." listing the open reports when a choice is required, or "Error: ..." (Power BI Desktop not running, no report open, or connection refused).</returns>
    public string ConnectPowerBiDesktop(string? pbixFileName = null)
    {
        lock (ConnLock)
        {
            var instances = PowerBiDesktopLocator.FindInstances();
            if (instances.Count == 0)
                return "Error: no Power BI Desktop instance found. Start Power BI Desktop and open a .pbix report, then retry.";

            PowerBiDesktopLocator.DiscoveredInstance match;
            if (!string.IsNullOrWhiteSpace(pbixFileName))
            {
                var wanted = pbixFileName.Trim();
                if (wanted.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
                    wanted = wanted[..^5];
                var hits = instances
                    .Where(i => string.Equals(i.DatabaseName, wanted, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hits.Count == 0)
                    return $"Error: no open Power BI Desktop report named '{wanted}'. Open reports: {string.Join(", ", instances.Select(i => i.DatabaseName))}.";
                match = hits[0];
            }
            else if (instances.Count == 1)
            {
                match = instances[0];
            }
            else
            {
                return "MultipleReports: several Power BI Desktop reports are open — pass pbixFileName. Open reports: "
                    + string.Join("; ", instances.Select(i => $"{i.DatabaseName} (port {i.Port})")) + ".";
            }

            if (_server != null && _port == match.Port && _databaseName == match.DatabaseName)
                return $"Already connected to model '{match.DatabaseName}' (localhost:{match.Port}).";

            DisconnectCore();
            try
            {
                var server = new Server();
                server.Connect($"localhost:{match.Port}");
                if (server.Databases.Count == 0)
                {
                    server.Disconnect();
                    return $"Error: localhost:{match.Port} answered but exposes no database.";
                }
                var db = server.Databases[0];
                _server = server;
                _db = db;
                _port = match.Port;
                _enginePid = match.EnginePid;
                _desktopPid = match.DesktopPid;
                _databaseName = match.DatabaseName;
                _engineStart = match.EngineStart;
                _fingerprint = $"{match.Port}|{match.EnginePid}|{match.EngineStart:O}|{match.DatabaseName}";
                Log.LogStep($"PowerBITool: connected to '{match.DatabaseName}' on localhost:{match.Port}");
                return $"Connected to model '{match.DatabaseName}' (localhost:{match.Port}). The live Power BI Desktop model is now readable and editable.";
            }
            catch (Exception ex)
            {
                return $"Error: could not connect to localhost:{match.Port} — {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Lists the Power BI Desktop reports currently open (each backed by its own local
    /// Analysis Services instance). Works without a connection — use it to discover
    /// pbixFileName values for ConnectPowerBiDesktop.
    /// </summary>
    /// <returns>One line per open report: name, process id, port; or "Error: no Power BI Desktop instance found."</returns>
    public string ListOpenReports()
    {
        var instances = PowerBiDesktopLocator.FindInstances();
        if (instances.Count == 0)
            return "Error: no Power BI Desktop instance found. Start Power BI Desktop and open a .pbix report.";
        var sb = new StringBuilder();
        foreach (var i in instances)
            sb.AppendLine($"{i.DatabaseName} — engine pid {i.EnginePid}, port {i.Port}"
                + (_databaseName == i.DatabaseName ? " [connected]" : ""));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reports the current connection: which model is targeted, on which port, and
    /// whether the connection is alive.
    /// </summary>
    /// <returns>"Connected: model '...' (localhost:PORT)" or "Not connected — call ConnectPowerBiDesktop first."</returns>
    public string GetConnectionStatus()
    {
        lock (ConnLock)
        {
            if (_server == null || _db == null)
                return "Not connected — call ConnectPowerBiDesktop first.";
            try
            {
                _ = _db.Model.Tables.Count;
                return $"Connected: model '{_databaseName}' (localhost:{_port}).";
            }
            catch (Exception ex)
            {
                return $"Stale connection (report likely closed): {ex.Message}. Reconnect with ConnectPowerBiDesktop.";
            }
        }
    }

    /// <summary>
    /// Closes the connection to the local Analysis Services instance. Does not close
    /// Power BI Desktop itself and does not touch the .pbix file.
    /// </summary>
    /// <returns>"Disconnected." always succeeds.</returns>
    public string Disconnect()
    {
        lock (ConnLock)
        {
            DisconnectCore();
            return "Disconnected.";
        }
    }

    private static void DisconnectCore()
    {
        try { _server?.Disconnect(); } catch { }
        _server = null;
        _db = null;
        _port = 0;
        _enginePid = 0;
        _desktopPid = 0;
        _databaseName = null;
        _fingerprint = null;
    }

    /// <summary>Returns the live database, verifying the session is still valid.
    /// Throws InvalidOperationException with a caller-facing message when not connected
    /// or when the engine behind the connection changed (port/pid recycled).</summary>
    private static Database RequireDb()
    {
        if (_db == null || _server == null)
            throw new InvalidOperationException("Not connected — call ConnectPowerBiDesktop first.");
        // Session identity: the engine process behind the port must still be the one we
        // connected to. Windows recycles ports and PIDs — a port answering again proves
        // nothing, so compare pid + start time before trusting a mutating call.
        try
        {
            using var engine = Process.GetProcessById(_enginePid);
            if (!string.Equals(engine.ProcessName, "msmdsrv", StringComparison.OrdinalIgnoreCase)
                || engine.StartTime != _engineStart)
                throw new InvalidOperationException(
                    "The Power BI Desktop session changed (engine restarted). Reconnect with ConnectPowerBiDesktop.");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "The Power BI Desktop engine is gone (report closed). Reconnect with ConnectPowerBiDesktop.");
        }
        return _db;
    }

    private static DateTime _engineStart;

    // ---- Inspection ----

    /// <summary>
    /// High-level overview of the connected semantic model: database name, compatibility
    /// level, counts of tables/measures/relationships, and per-table type with row counts.
    /// Use this as the first step before any modeling or DAX work.
    /// </summary>
    /// <returns>Markdown summary, or "Error: not connected ...".</returns>
    public string GetModelSummary()
    {
        try
        {
            lock (ConnLock)
            {
                var db = RequireDb();
                var model = db.Model;
                int measures = model.Tables.Sum(t => t.Measures.Count);
                var rowCounts = GetRowCounts(model);
                var sb = new StringBuilder();
                sb.AppendLine($"Model: {db.Name}");
                sb.AppendLine($"Compatibility level: {db.CompatibilityLevel} ({db.CompatibilityMode})");
                sb.AppendLine($"Tables: {VisibleTables(model).Count()} | Measures: {measures} | Relationships: {model.Relationships.Count}");
                sb.AppendLine();
                sb.AppendLine("| Table | Type | Columns | Measures | Rows |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var t in VisibleTables(model).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"| {t.Name} | {DescribeTableType(t)} | {t.Columns.Count} | {t.Measures.Count} | {rowCounts.GetValueOrDefault(t.Name, "?")} |");
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Lists every table in the model with its type (Calculated / Imported / DirectQuery)
    /// and row count. Auto-date/system tables (DateTableTemplate_, LocalDateTable_) are
    /// excluded — they are internal artifacts of the auto date/time feature.
    /// </summary>
    /// <returns>One line per table, or "Error: not connected ...".</returns>
    public string ListTables()
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var rowCounts = GetRowCounts(model);
                var sb = new StringBuilder();
                foreach (var t in VisibleTables(model).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"{t.Name} — {DescribeTableType(t)}, {rowCounts.GetValueOrDefault(t.Name, "?")} rows, {t.Columns.Count} columns, {t.Measures.Count} measures");
                return sb.Length == 0 ? "The model has no tables." : sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Full schema of one table: every column (data type, role, format, description)
    /// and every measure (expression, format string, display folder).
    /// </summary>
    /// <param name="tableName">Exact table name as shown by ListTables.</param>
    /// <returns>Markdown schema, or "Error: table '...' not found ...".</returns>
    public string GetTableSchema(string tableName)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found. Available: {string.Join(", ", VisibleTables(model).Select(x => x.Name))}.";
                var sb = new StringBuilder();
                sb.AppendLine($"Table {t.Name} — {DescribeTableType(t)}" + (string.IsNullOrWhiteSpace(t.Description) ? "" : $" — {t.Description}"));
                sb.AppendLine();
                sb.AppendLine("| Column | Type | Role | Format | Description |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var c in t.Columns)
                    sb.AppendLine($"| {c.Name} | {c.DataType} | {c.Type} | {c.FormatString ?? ""} | {c.Description ?? ""} |");
                if (t.Measures.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| Measure | Format | Display folder | Expression |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var m in t.Measures)
                        sb.AppendLine($"| {m.Name} | {m.FormatString ?? ""} | {m.DisplayFolder ?? ""} | {m.Expression} |");
                }
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Lists all relationships in the model: from table.column → to table.column,
    /// cardinality, cross-filter direction, active/inactive.
    /// </summary>
    /// <returns>One line per relationship, or "Error: not connected ...".</returns>
    public string ListRelationships()
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                if (model.Relationships.Count == 0) return "The model has no relationships.";
                var sb = new StringBuilder();
                foreach (var r in model.Relationships)
                {
                    if (r is SingleColumnRelationship scr)
                    {
                        var from = $"{r.FromTable.Name}.{scr.FromColumn.Name}";
                        var to = $"{r.ToTable.Name}.{scr.ToColumn.Name}";
                        sb.AppendLine($"{from} → {to} | {scr.FromCardinality}→{scr.ToCardinality} | {r.CrossFilteringBehavior} | {(r.IsActive ? "active" : "inactive")}");
                    }
                    else
                    {
                        sb.AppendLine($"{r.FromTable.Name} → {r.ToTable.Name} | {r.Type} | {r.CrossFilteringBehavior} | {(r.IsActive ? "active" : "inactive")}");
                    }
                }
                return sb.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Reads one measure: DAX expression, format string, description, display folder.
    /// </summary>
    /// <param name="tableName">Table that hosts the measure.</param>
    /// <param name="measureName">Measure name.</param>
    /// <returns>Measure details, or "Error: ...".</returns>
    public string GetMeasure(string tableName, string measureName)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                var m = t.Measures.FirstOrDefault(x => string.Equals(x.Name, measureName, StringComparison.OrdinalIgnoreCase));
                if (m == null) return $"Error: measure '{measureName}' not found in '{tableName}'. Measures: {string.Join(", ", t.Measures.Select(x => x.Name))}.";
                return $"Measure {t.Name}[{m.Name}]\nExpression: {m.Expression}\nFormat: {m.FormatString ?? "(none)"}\nDisplay folder: {m.DisplayFolder ?? "(none)"}\nDescription: {m.Description ?? "(none)"}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ---- shared helpers ----

    /// <summary>Model tables excluding auto-date/system artifacts.</summary>
    internal static IEnumerable<Table> VisibleTables(Model model)
        => model.Tables.Where(t => !t.Name.StartsWith("DateTableTemplate_", StringComparison.OrdinalIgnoreCase)
                               && !t.Name.StartsWith("LocalDateTable_", StringComparison.OrdinalIgnoreCase));

    internal static Table? FindTable(Model model, string name)
        => model.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static string DescribeTableType(Table t)
    {
        var part = t.Partitions.FirstOrDefault();
        if (part == null) return "Empty";
        if (part.Mode == ModeType.DirectQuery) return "DirectQuery";
        if (part.Mode == ModeType.DirectLake) return "DirectLake";
        return part.SourceType switch
        {
            PartitionSourceType.Calculated => "Calculated",
            PartitionSourceType.M => "Imported (Power Query)",
            PartitionSourceType.Query => "Imported (query)",
            PartitionSourceType.Entity => "Imported (entity)",
            PartitionSourceType.Parquet => "DirectLake (parquet)",
            PartitionSourceType.CalculationGroup => "Calculation group",
            _ => part.SourceType.ToString()
        };
    }

    internal static bool IsCalculatedTable(Table t)
        => t.Partitions.Any(p => p.SourceType == PartitionSourceType.Calculated);

    /// <summary>Row counts for every visible table in one DAX round trip
    /// (TOM does not expose row counts; the engine does).</summary>
    private static Dictionary<string, string> GetRowCounts(Model model)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tables = VisibleTables(model).ToList();
        if (tables.Count == 0) return result;
        try
        {
            var parts = tables.Select(t => $"ROW(\"t\", \"{t.Name.Replace("\"", "\"\"")}\", \"n\", COUNTROWS('{t.Name}'))");
            string q = "EVALUATE UNION(" + string.Join(", ", parts) + ")";
            using var conn = OpenAdomd();
            using var cmd = new Microsoft.AnalysisServices.AdomdClient.AdomdCommand(q, conn) { CommandTimeout = 120 };
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = r[1]?.ToString() ?? "?";
        }
        catch { /* tables that fail to count stay "?" */ }
        return result;
    }
}
