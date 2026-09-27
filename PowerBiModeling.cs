using System.Text;
using AIOrchestrator;
using Microsoft.AnalysisServices.Tabular;

namespace PowerBIToolNS;

/// <summary>
/// Modeling operations on the live Power BI Desktop semantic model via TOM. Every
/// mutation is applied to the in-memory model and saved with a single SaveChanges
/// (validate-first, save-once). Changes are immediately visible in Power BI Desktop
/// but ephemeral until the user presses Ctrl+S (see LiveNote).
/// </summary>
public partial class PowerBITool
{
    /// <summary>
    /// Creates a DAX measure in a table. The measure appears immediately in the
    /// Power BI Desktop Fields pane. Validate the expression first with ValidateDax
    /// to avoid creating broken measures.
    /// </summary>
    /// <param name="tableName">Existing table that will host the measure.</param>
    /// <param name="measureName">Measure name (no square brackets).</param>
    /// <param name="daxExpression">DAX expression, e.g. "SUM(Sales[Amount])".</param>
    /// <param name="formatString">Optional format string, e.g. "#,##0.00" or "$#,##0". Strongly recommended.</param>
    /// <param name="description">Optional description shown in tooltips and the data dictionary. Strongly recommended.</param>
    /// <param name="displayFolder">Optional folder in the Fields pane for organization.</param>
    /// <returns>Confirmation with the measure reference + persistence note, or "Error: ...".</returns>
    public string CreateMeasure(string tableName, string measureName, string daxExpression,
        string? formatString = null, string? description = null, string? displayFolder = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                if (string.IsNullOrWhiteSpace(measureName)) return "Error: measureName is required.";
                if (measureName.IndexOfAny(new[] { '[', ']', '\n', '\r' }) >= 0)
                    return "Error: measureName must not contain brackets or newlines.";
                if (t.Measures.Any(m => string.Equals(m.Name, measureName, StringComparison.OrdinalIgnoreCase)))
                    return $"Error: measure '{measureName}' already exists in '{tableName}'. Use UpdateMeasure.";
                if (string.IsNullOrWhiteSpace(daxExpression)) return "Error: daxExpression is required.";

                var m = new Measure
                {
                    Name = measureName,
                    Expression = daxExpression,
                    FormatString = formatString,
                    Description = description,
                    DisplayFolder = displayFolder
                };
                t.Measures.Add(m);
                RequireDb().Model.SaveChanges();
                Log.LogStep($"PowerBITool: created measure '{tableName}[{measureName}]'");
                return $"Created measure '{tableName}[{measureName}]' = {daxExpression}.{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Updates an existing measure: expression, format string, description, display
    /// folder, and/or rename. Only the provided fields change; omitted fields stay as is.
    /// </summary>
    /// <param name="tableName">Table hosting the measure.</param>
    /// <param name="measureName">Current measure name.</param>
    /// <param name="daxExpression">New DAX expression, or null to keep the current one.</param>
    /// <param name="formatString">New format string, or null to keep.</param>
    /// <param name="description">New description, or null to keep.</param>
    /// <param name="displayFolder">New display folder, or null to keep.</param>
    /// <param name="newName">New measure name, or null to keep. Renaming a measure referenced by report visuals can break them in Power BI Desktop.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string UpdateMeasure(string tableName, string measureName, string? daxExpression = null,
        string? formatString = null, string? description = null, string? displayFolder = null, string? newName = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                var m = t.Measures.FirstOrDefault(x => string.Equals(x.Name, measureName, StringComparison.OrdinalIgnoreCase));
                if (m == null) return $"Error: measure '{measureName}' not found in '{tableName}'.";

                var changed = new List<string>();
                if (!string.IsNullOrWhiteSpace(daxExpression)) { m.Expression = daxExpression; changed.Add("expression"); }
                if (formatString != null) { m.FormatString = formatString; changed.Add("format"); }
                if (description != null) { m.Description = description; changed.Add("description"); }
                if (displayFolder != null) { m.DisplayFolder = displayFolder; changed.Add("display folder"); }
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    if (t.Measures.Any(x => !ReferenceEquals(x, m) && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
                        return $"Error: measure '{newName}' already exists in '{tableName}'.";
                    m.Name = newName; changed.Add($"renamed to '{newName}'");
                }
                if (changed.Count == 0) return "Error: nothing to update — all fields were null/empty.";

                RequireDb().Model.SaveChanges();
                Log.LogStep($"PowerBITool: updated measure '{tableName}[{measureName}]' ({string.Join(", ", changed)})");
                return $"Updated measure '{tableName}[{measureName}]': {string.Join(", ", changed)}.{LiveNote}"
                    + (changed.Any(c => c.StartsWith("renamed")) ? " WARNING: renamed measures referenced by report visuals may break in Power BI Desktop." : "");
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Deletes a measure from the model.
    /// </summary>
    /// <param name="tableName">Table hosting the measure.</param>
    /// <param name="measureName">Measure to delete.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string DeleteMeasure(string tableName, string measureName)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                var m = t.Measures.FirstOrDefault(x => string.Equals(x.Name, measureName, StringComparison.OrdinalIgnoreCase));
                if (m == null) return $"Error: measure '{measureName}' not found in '{tableName}'.";
                t.Measures.Remove(m);
                RequireDb().Model.SaveChanges();
                Log.LogStep($"PowerBITool: deleted measure '{tableName}[{measureName}]'");
                return $"Deleted measure '{tableName}[{measureName}]'.{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Creates a calculated table from a DAX expression. The table and its rows appear
    /// immediately in the model and Fields pane.
    /// </summary>
    /// <param name="tableName">New table name.</param>
    /// <param name="daxExpression">DAX table expression, e.g. "SUMMARIZE(Sales, Sales[Region], \"Total\", SUM(Sales[Amount]))".</param>
    /// <param name="description">Optional table description.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string CreateCalculatedTable(string tableName, string daxExpression, string? description = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                if (string.IsNullOrWhiteSpace(tableName)) return "Error: tableName is required.";
                if (string.IsNullOrWhiteSpace(daxExpression)) return "Error: daxExpression is required.";
                if (FindTable(model, tableName) != null)
                    return $"Error: table '{tableName}' already exists. Delete it first or choose another name.";

                var t = new Table { Name = tableName, Description = description };
                var part = new Partition
                {
                    Name = tableName,
                    Source = new CalculatedPartitionSource { Expression = daxExpression }
                };
                t.Partitions.Add(part);
                model.Tables.Add(t);
                RequireDb().Model.SaveChanges();
                // A calculated table is empty until the engine computes it: request a
                // full refresh so rows materialize and DAX queries against it work.
                try
                {
                    part.RequestRefresh(RefreshType.Full);
                    RequireDb().Model.SaveChanges();
                }
                catch (Exception pex)
                {
                    return $"Created calculated table '{tableName}' but it is NOT yet computed — call RefreshTable('{tableName}') or refresh in Power BI Desktop. ({pex.Message}){LiveNote}";
                }
                Log.LogStep($"PowerBITool: created calculated table '{tableName}'");
                return $"Created calculated table '{tableName}' (computed).{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Creates a calculated column (DAX) in an existing table.
    /// </summary>
    /// <param name="tableName">Existing table.</param>
    /// <param name="columnName">New column name.</param>
    /// <param name="dataType">Column data type: Int64, Double, Decimal, String, Boolean, DateTime.</param>
    /// <param name="daxExpression">DAX row expression.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string CreateCalculatedColumn(string tableName, string columnName, string dataType,
        string daxExpression, string? description = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                if (string.IsNullOrWhiteSpace(columnName)) return "Error: columnName is required.";
                if (t.Columns.Any(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase)))
                    return $"Error: column '{columnName}' already exists in '{tableName}'.";
                if (!Enum.TryParse<DataType>(dataType, true, out var dt))
                    return $"Error: invalid dataType '{dataType}'. Use Int64, Double, Decimal, String, Boolean or DateTime.";

                var col = new CalculatedColumn
                {
                    Name = columnName,
                    DataType = dt,
                    Expression = daxExpression,
                    Description = description
                };
                t.Columns.Add(col);
                RequireDb().Model.SaveChanges();
                // A calculated column has no values until the engine computes it:
                // request a recalculation so queries against it work immediately.
                try
                {
                    t.RequestRefresh(RefreshType.Calculate);
                    RequireDb().Model.SaveChanges();
                }
                catch (Exception rex)
                {
                    return $"Created calculated column '{tableName}[{columnName}]' but it is NOT yet computed — call RefreshTable('{tableName}') or refresh in Power BI Desktop. ({rex.Message}){LiveNote}";
                }
                Log.LogStep($"PowerBITool: created calculated column '{tableName}[{columnName}]'");
                return $"Created calculated column '{tableName}[{columnName}]' ({dt}, computed).{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Creates a relationship between two columns (star-schema join).
    /// </summary>
    /// <param name="fromTable">Many-side table (e.g. Sales).</param>
    /// <param name="fromColumn">Many-side key column.</param>
    /// <param name="toTable">One-side table (e.g. Product).</param>
    /// <param name="toColumn">One-side key column.</param>
    /// <param name="active">True for an active relationship (default).</param>
    /// <param name="crossFilterDirection">Single (default, recommended), Both (bidirectional — use sparingly), OneDirectionalCrossFilter or BothCrossFilterObjectLevel.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string CreateRelationship(string fromTable, string fromColumn, string toTable, string toColumn,
        bool active = true, string crossFilterDirection = "Single")
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var ft = FindTable(model, fromTable);
                var tt = FindTable(model, toTable);
                if (ft == null) return $"Error: fromTable '{fromTable}' not found.";
                if (tt == null) return $"Error: toTable '{toTable}' not found.";
                var fc = ft.Columns.FirstOrDefault(c => string.Equals(c.Name, fromColumn, StringComparison.OrdinalIgnoreCase));
                var tc = tt.Columns.FirstOrDefault(c => string.Equals(c.Name, toColumn, StringComparison.OrdinalIgnoreCase));
                if (fc == null) return $"Error: fromColumn '{fromColumn}' not found in '{fromTable}'.";
                if (tc == null) return $"Error: toColumn '{toColumn}' not found in '{toTable}'.";
                if (!Enum.TryParse<CrossFilteringBehavior>(crossFilterDirection, true, out var cfd))
                {
                    // Friendly aliases: "Single"/"Both" map to the enum's OneDirection/BothDirections.
                    cfd = crossFilterDirection.ToLowerInvariant() switch
                    {
                        "single" => CrossFilteringBehavior.OneDirection,
                        "both" => CrossFilteringBehavior.BothDirections,
                        "automatic" => CrossFilteringBehavior.Automatic,
                        _ => CrossFilteringBehavior.OneDirection
                    };
                    if (crossFilterDirection.ToLowerInvariant() is not ("single" or "both" or "automatic"))
                        return $"Error: invalid crossFilterDirection '{crossFilterDirection}'. Use Single, Both or Automatic.";
                }

                // Reject only active-vs-active duplicates: two ACTIVE relationships over
                // the same column pair (either direction) create an ambiguous filter path
                // that makes every later SaveChanges fail. An inactive relationship is the
                // normal USERELATIONSHIP pattern and never causes ambiguity, so it is allowed.
                if (active)
                {
                    bool conflict = model.Relationships.OfType<SingleColumnRelationship>()
                        .Where(r => r.IsActive)
                        .Any(r =>
                            (string.Equals(r.FromTable.Name, fromTable, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.FromColumn.Name, fromColumn, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.ToTable.Name, toTable, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.ToColumn.Name, toColumn, StringComparison.OrdinalIgnoreCase))
                            || (string.Equals(r.FromTable.Name, toTable, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.FromColumn.Name, toColumn, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.ToTable.Name, fromTable, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.ToColumn.Name, fromColumn, StringComparison.OrdinalIgnoreCase)));
                    if (conflict)
                        return $"Error: an active relationship between {fromTable}[{fromColumn}] and {toTable}[{toColumn}] already exists. Delete it first (DeleteRelationship) or inspect with ListRelationships.";
                }

                // Infer cardinality from the actual data instead of trusting the caller's
                // from/to direction: a column is the "one" side when its values are unique.
                // This makes the tool forgiving of reversed endpoints and avoids the cryptic
                // engine "duplicate value not allowed on the one side" error.
                long fromDistinct = ScalarLong($"EVALUATE ROW(\"d\", DISTINCTCOUNT('{fromTable}'[{fromColumn}]))");
                long fromRows = ScalarLong($"EVALUATE ROW(\"d\", COUNTROWS('{fromTable}'))");
                long toDistinct = ScalarLong($"EVALUATE ROW(\"d\", DISTINCTCOUNT('{toTable}'[{toColumn}]))");
                long toRows = ScalarLong($"EVALUATE ROW(\"d\", COUNTROWS('{toTable}'))");
                bool fromOne = fromRows > 0 && fromDistinct == fromRows;
                bool toOne = toRows > 0 && toDistinct == toRows;
                if (!fromOne && !toOne)
                    return $"Error: neither {fromTable}[{fromColumn}] nor {toTable}[{toColumn}] is unique — a relationship needs at least one unique (\"one\") side; many-to-many is not supported.";
                if (fromOne && !toOne)
                    return $"Error: '{fromTable}[{fromColumn}]' is unique but '{toTable}[{toColumn}]' has duplicates — a relationship must run from the many side to the one side. Swap the arguments: CreateRelationship(\"{toTable}\", \"{toColumn}\", \"{fromTable}\", \"{fromColumn}\").";
                var fromCard = fromOne ? RelationshipEndCardinality.One : RelationshipEndCardinality.Many;
                var toCard = toOne ? RelationshipEndCardinality.One : RelationshipEndCardinality.Many;

                var rel = new SingleColumnRelationship
                {
                    FromColumn = fc,
                    ToColumn = tc,
                    FromCardinality = fromCard,
                    ToCardinality = toCard,
                    IsActive = active,
                    CrossFilteringBehavior = cfd,
                    SecurityFilteringBehavior = SecurityFilteringBehavior.OneDirection
                };
                model.Relationships.Add(rel);
                RequireDb().Model.SaveChanges();
                // The join data for a new relationship is built during a calculation pass:
                // recalc both tables so cross-table queries (RELATED, cross-filter) work
                // immediately. Non-fatal — the relationship exists even if recalc is deferred.
                try
                {
                    ft.RequestRefresh(RefreshType.Calculate);
                    tt.RequestRefresh(RefreshType.Calculate);
                    RequireDb().Model.SaveChanges();
                }
                catch (Exception rex)
                {
                    Log.LogStep($"PowerBITool: relationship created but recalc deferred: {rex.Message}");
                }
                Log.LogStep($"PowerBITool: created relationship {fromTable}[{fromColumn}] → {toTable}[{toColumn}]");
                return $"Created relationship {fromTable}[{fromColumn}] → {toTable}[{toColumn}] ({cfd}, {fromCard}→{toCard}, {(active ? "active" : "inactive")}).{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Deletes a relationship by its endpoints (from table.column → to table.column).
    /// </summary>
    /// <param name="fromTable">From-side table.</param>
    /// <param name="fromColumn">From-side column.</param>
    /// <param name="toTable">To-side table.</param>
    /// <param name="toColumn">To-side column.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string DeleteRelationship(string fromTable, string fromColumn, string toTable, string toColumn)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var rel = model.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r =>
                    string.Equals(r.FromTable.Name, fromTable, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.FromColumn.Name, fromColumn, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.ToTable.Name, toTable, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.ToColumn.Name, toColumn, StringComparison.OrdinalIgnoreCase));
                if (rel == null) return "Error: no matching relationship found. Check ListRelationships for exact names.";
                model.Relationships.Remove(rel);
                RequireDb().Model.SaveChanges();
                Log.LogStep($"PowerBITool: deleted relationship {fromTable}[{fromColumn}] → {toTable}[{toColumn}]");
                return $"Deleted relationship {fromTable}[{fromColumn}] → {toTable}[{toColumn}].{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Sets or clears the description of a table, column or measure (documentation).
    /// </summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="description">Description text; empty string clears it.</param>
    /// <param name="columnName">Optional column name (when documenting a column).</param>
    /// <param name="measureName">Optional measure name (when documenting a measure).</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string SetDescription(string tableName, string description, string? columnName = null, string? measureName = null)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                string target;
                if (measureName != null)
                {
                    var m = t.Measures.FirstOrDefault(x => string.Equals(x.Name, measureName, StringComparison.OrdinalIgnoreCase));
                    if (m == null) return $"Error: measure '{measureName}' not found in '{tableName}'.";
                    m.Description = description;
                    target = $"measure '{tableName}[{measureName}]'";
                }
                else if (columnName != null)
                {
                    var c = t.Columns.FirstOrDefault(x => string.Equals(x.Name, columnName, StringComparison.OrdinalIgnoreCase));
                    if (c == null) return $"Error: column '{columnName}' not found in '{tableName}'.";
                    c.Description = description;
                    target = $"column '{tableName}[{columnName}]'";
                }
                else
                {
                    t.Description = description;
                    target = $"table '{tableName}'";
                }
                RequireDb().Model.SaveChanges();
                return $"Set description on {target}.{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Deletes a calculated table from the model. Only calculated tables can be deleted
    /// this way — imported/Power Query tables are protected (their data comes from
    /// external sources and deleting them over the live connection is not supported).
    /// </summary>
    /// <param name="tableName">Calculated table to delete.</param>
    /// <returns>Confirmation + persistence note, or "Error: ...".</returns>
    public string DeleteTable(string tableName)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                if (!IsCalculatedTable(t))
                    return $"Error: '{tableName}' is {DescribeTableType(t)} — only Calculated tables can be deleted. Imported/Power Query tables must be removed in Power BI Desktop.";
                // Cascade: remove relationships that point at this table together with the
                // table itself in a single SaveChanges, so the model never lands in a
                // half-deleted (ambiguous-path) state.
                var referencing = model.Relationships
                    .Where(r => string.Equals(r.FromTable?.Name, tableName, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(r.ToTable?.Name, tableName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var r in referencing) model.Relationships.Remove(r);
                model.Tables.Remove(t);
                RequireDb().Model.SaveChanges();
                Log.LogStep($"PowerBITool: deleted calculated table '{tableName}' ({referencing.Count} relationship(s) cascaded)");
                string cascade = referencing.Count > 0
                    ? $" Cascaded delete of {referencing.Count} relationship(s): {string.Join("; ", referencing.Select(r => $"{r.FromTable.Name} → {r.ToTable.Name}"))}."
                    : "";
                return $"Deleted calculated table '{tableName}'.{cascade}{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Recomputes a calculated table so its rows materialize in the engine (a newly
    /// created calculated table is empty until processed). Use after CreateCalculatedTable
    /// if the table was reported as not computed, or to force a fresh computation.
    /// </summary>
    /// <param name="tableName">Calculated table to recompute.</param>
    /// <returns>Confirmation with the new row count, or "Error: ...".</returns>
    public string RefreshTable(string tableName)
    {
        try
        {
            lock (ConnLock)
            {
                var model = RequireDb().Model;
                var t = FindTable(model, tableName);
                if (t == null) return $"Error: table '{tableName}' not found.";
                if (!IsCalculatedTable(t))
                    return $"Error: '{tableName}' is {DescribeTableType(t)} — only Calculated tables can be recomputed this way. Power Query tables must be refreshed in Power BI Desktop.";
                foreach (var p in t.Partitions.Where(p => p.SourceType == PartitionSourceType.Calculated))
                    p.RequestRefresh(RefreshType.Full);
                RequireDb().Model.SaveChanges();
                var rows = GetRowCounts(model).GetValueOrDefault(tableName, "?");
                Log.LogStep($"PowerBITool: refreshed calculated table '{tableName}' ({rows} rows)");
                return $"Recomputed calculated table '{tableName}' — {rows} rows.{LiveNote}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
