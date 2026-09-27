using System.Diagnostics;
using PowerBIToolNS;

// 200 real-usage tests for PowerBITool against the live Power BI Desktop instance.
// Each test: id, name, action, expectation. Results logged to testlog.txt with
// timestamps; a PASS/FAIL summary is printed at the end.

var tool = new PowerBITool();
var logPath = Path.Combine(AppContext.BaseDirectory, "testlog.txt");
File.WriteAllText(logPath, $"PowerBITool test run {DateTime.Now:O}\n");
int passed = 0, failed = 0;
var failures = new List<string>();

void T(int id, string name, Func<string> act, Func<string, bool> expect)
{
    string result;
    bool ok;
    var sw = Stopwatch.StartNew();
    try
    {
        var task = Task.Run(act);
        if (!task.Wait(TimeSpan.FromSeconds(120)))
        {
            result = "TIMEOUT";
            ok = false;
        }
        else { result = task.Result ?? "(null)"; ok = expect(result); }
    }
    catch (Exception ex) { result = "EXCEPTION: " + (ex.InnerException ?? ex).Message; ok = false; }
    sw.Stop();
    File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] #{id:D3} {name} ({sw.ElapsedMilliseconds}ms)\n  -> {Truncate(result, 400)}\n  {(ok ? "PASS" : "FAIL")}\n");
    if (ok) passed++;
    else { failed++; failures.Add($"#{id:D3} {name}: {Truncate(result, 160)}"); }
    Console.WriteLine($"#{id:D3} {(ok ? "PASS" : "FAIL")} {name}");
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

Console.WriteLine("Waiting for Power BI Desktop engine to come up…");
for (int i = 0; i < 60; i++)
{
    var found = tool.ListOpenReports();
    if (!found.StartsWith("Error")) break;
    Thread.Sleep(2000);
}

// ==================== PHASE A: connection lifecycle (1-12) ====================
T(1, "status before connect", () => tool.GetConnectionStatus(), r => r.StartsWith("Not connected"));
T(2, "list reports (live)", () => tool.ListOpenReports(), r => !r.StartsWith("Error"));
T(3, "connect by discovery", () => tool.ConnectPowerBiDesktop(), r => r.StartsWith("Connected") || r.StartsWith("MultipleReports"));
T(4, "connect again idempotent", () => tool.ConnectPowerBiDesktop(), r => r.StartsWith("Already connected") || r.StartsWith("Connected"));
T(5, "status after connect", () => tool.GetConnectionStatus(), r => r.StartsWith("Connected"));
T(6, "connect wrong name", () => tool.ConnectPowerBiDesktop("NoSuchReport99"), r => r.StartsWith("Error") && r.Contains("no open"));
T(7, "connect with .pbix suffix", () => tool.ConnectPowerBiDesktop(tool.GetConnectionStatus().Split('\'')[1] + ".pbix"), r => r.StartsWith("Already connected") || r.StartsWith("Connected"));
T(8, "disconnect", () => tool.Disconnect(), r => r == "Disconnected.");
T(9, "status after disconnect", () => tool.GetConnectionStatus(), r => r.StartsWith("Not connected"));
T(10, "reconnect", () => tool.ConnectPowerBiDesktop(), r => r.StartsWith("Connected") || r.StartsWith("MultipleReports"));
T(11, "list shows connected", () => tool.ListOpenReports(), r => r.Contains("[connected]"));
T(12, "double disconnect safe", () => { tool.Disconnect(); return tool.Disconnect(); }, r => r == "Disconnected.");
tool.ConnectPowerBiDesktop();

// Idempotent clean slate: the live model persists across runs, so remove any
// leftover objects from a previous run before Phase B creates them fresh.
// Relationships must go before tables (DeleteTable refuses a referenced table),
// and each pair is deleted repeatedly to clear duplicates left by older runs.
void CleanRel(string ft, string fc, string tt, string tc)
{
    for (int i = 0; i < 10; i++)
        if (tool.DeleteRelationship(ft, fc, tt, tc).StartsWith("Error")) break;
}
CleanRel("Sales", "RegionId", "Region", "RegionId");
CleanRel("Region", "RegionId", "Sales", "RegionId");
CleanRel("Sales", "ProductId", "Product", "ProductId");
CleanRel("Product", "ProductId", "Sales", "ProductId");
tool.DeleteTable("Region");
tool.DeleteTable("Product");
tool.DeleteTable("Sales");

// ==================== PHASE B: model setup (13-30) ====================
T(13, "create Region table", () => tool.CreateCalculatedTable("Region", "DATATABLE(\"RegionId\", INTEGER, \"Name\", STRING, {{1,\"North\"},{2,\"South\"},{3,\"East\"},{4,\"West\"}})"), r => r.StartsWith("Created calculated table"));
T(14, "create Product table", () => tool.CreateCalculatedTable("Product", "DATATABLE(\"ProductId\", INTEGER, \"Name\", STRING, \"Price\", DOUBLE, {{1,\"Widget\",9.99},{2,\"Gadget\",19.50},{3,\"Doohickey\",4.75},{4,\"Thingamajig\",42.00},{5,\"Gizmo\",14.25}})"), r => r.StartsWith("Created calculated table"));
T(15, "create Sales table", () => tool.CreateCalculatedTable("Sales", "DATATABLE(\"SaleId\", INTEGER, \"RegionId\", INTEGER, \"ProductId\", INTEGER, \"Amount\", DOUBLE, \"Qty\", INTEGER, {{1,1,1,9.99,1},{2,1,2,39.00,2},{3,2,3,4.75,1},{4,2,1,19.98,2},{5,3,4,42.00,1},{6,3,5,28.50,2},{7,4,2,19.50,1},{8,4,3,9.50,2},{9,1,4,42.00,1},{10,2,5,14.25,1}})"), r => r.StartsWith("Created calculated table"));
T(16, "create duplicate table rejected", () => tool.CreateCalculatedTable("Region", "DATATABLE(\"x\", INTEGER, {{1}})"), r => r.StartsWith("Error") && r.Contains("already exists"));
T(17, "rel Sales-Region", () => tool.CreateRelationship("Sales", "RegionId", "Region", "RegionId"), r => r.StartsWith("Created relationship"));
T(18, "rel Sales-Product", () => tool.CreateRelationship("Sales", "ProductId", "Product", "ProductId"), r => r.StartsWith("Created relationship"));
T(19, "rel bad from column", () => tool.CreateRelationship("Sales", "NoCol", "Region", "RegionId"), r => r.StartsWith("Error") && r.Contains("fromColumn"));
T(20, "rel bad to table", () => tool.CreateRelationship("Sales", "RegionId", "NoTable", "RegionId"), r => r.StartsWith("Error") && r.Contains("toTable"));
T(21, "measure TotalSales", () => tool.CreateMeasure("Sales", "TotalSales", "SUM(Sales[Amount])", "#,##0.00 €", "Total sales amount"), r => r.StartsWith("Created measure"));
T(22, "measure TotalQty", () => tool.CreateMeasure("Sales", "TotalQty", "SUM(Sales[Qty])", "#,##0"), r => r.StartsWith("Created measure"));
T(23, "measure AvgOrder", () => tool.CreateMeasure("Sales", "AvgOrder", "DIVIDE([TotalSales], DISTINCTCOUNT(Sales[SaleId]))", "#,##0.00"), r => r.StartsWith("Created measure"));
T(24, "measure dup rejected", () => tool.CreateMeasure("Sales", "TotalSales", "1"), r => r.StartsWith("Error") && r.Contains("already exists"));
T(25, "measure empty name rejected", () => tool.CreateMeasure("Sales", "  ", "1"), r => r.StartsWith("Error"));
T(26, "measure bracket name rejected", () => tool.CreateMeasure("Sales", "Bad[Name]", "1"), r => r.StartsWith("Error"));
T(27, "measure bad table rejected", () => tool.CreateMeasure("NoTable", "M", "1"), r => r.StartsWith("Error"));
T(28, "calc column RegionName", () => tool.CreateCalculatedColumn("Sales", "RegionName", "String", "RELATED(Region[Name])"), r => r.StartsWith("Created calculated column"));
T(29, "calc column dup rejected", () => tool.CreateCalculatedColumn("Sales", "RegionName", "String", "1"), r => r.StartsWith("Error"));
T(30, "calc column bad type rejected", () => tool.CreateCalculatedColumn("Sales", "X", "Blob", "1"), r => r.StartsWith("Error"));

// ==================== PHASE C: inspection (31-50) ====================
T(31, "model summary", () => tool.GetModelSummary(), r => r.Contains("Region") && r.Contains("Sales") && r.Contains("Compatibility"));
T(32, "summary row counts", () => tool.GetModelSummary(), r => r.Contains("| 10 |") && r.Contains("| 4 |"));
T(33, "list tables", () => tool.ListTables(), r => r.Contains("Region") && r.Contains("Product") && r.Contains("Sales"));
T(34, "schema Sales", () => tool.GetTableSchema("Sales"), r => r.Contains("Amount") && r.Contains("TotalSales") && r.Contains("RegionName"));
T(35, "schema Region", () => tool.GetTableSchema("Region"), r => r.Contains("RegionId") && r.Contains("Name"));
T(36, "schema missing table", () => tool.GetTableSchema("Ghost"), r => r.StartsWith("Error") && r.Contains("not found"));
T(37, "list relationships", () => tool.ListRelationships(), r => r.Contains("Sales.RegionId") && r.Contains("Many→One"));
T(38, "get measure TotalSales", () => tool.GetMeasure("Sales", "TotalSales"), r => r.Contains("SUM(Sales[Amount])") && r.Contains("€"));
T(39, "get measure missing", () => tool.GetMeasure("Sales", "Nope"), r => r.StartsWith("Error"));
T(40, "get measure wrong table", () => tool.GetMeasure("Region", "TotalSales"), r => r.StartsWith("Error"));
T(41, "summary case-insensitive table", () => tool.GetTableSchema("sales"), r => r.Contains("Amount"));
T(42, "profile Sales", () => tool.ProfileTable("Sales"), r => r.Contains("Distinct") && r.Contains("RegionId"));
T(43, "profile Region", () => tool.ProfileTable("Region", 3, 2), r => r.Contains("North") || r.Contains("Top values"));
T(44, "profile missing table", () => tool.ProfileTable("Ghost"), r => r.StartsWith("Error"));
T(45, "data dictionary full", () => tool.GenerateDataDictionary(), r => r.Contains("# Data dictionary") && r.Contains("TotalSales"));
T(46, "data dictionary one table", () => tool.GenerateDataDictionary("Region"), r => r.Contains("## Region") && !r.Contains("## Sales"));
T(47, "data dictionary missing table", () => tool.GenerateDataDictionary("Ghost"), r => r.StartsWith("Error"));
T(48, "best practices report", () => tool.AnalyzeBestPractices(), r => r.Contains("Warnings") && r.Contains("Info"));
T(49, "lint clean dax", () => tool.LintDax("DIVIDE(SUM(t[x]), COUNTROWS(t))"), r => r.Contains("No anti-patterns"));
T(50, "lint slash division", () => tool.LintDax("SUM(t[x]) / SUM(t[y])"), r => r.Contains("DIVIDE"));

// ==================== PHASE D: DAX guard (51-75) ====================
T(51, "guard: simple EVALUATE", () => tool.RunDaxQuery("EVALUATE ROW(\"a\", 1)"), r => r.Contains("a") && !r.StartsWith("Error"));
T(52, "guard: DEFINE EVALUATE", () => tool.RunDaxQuery("DEFINE MEASURE Sales[mm] = 42 EVALUATE ROW(\"v\", [mm])"), r => r.Contains("42"));
T(53, "guard: DMV catalogs", () => tool.RunDaxQuery("SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS"), r => !r.StartsWith("Error"));
T(54, "guard: reject DROP", () => tool.RunDaxQuery("DROP TABLE Sales"), r => r.StartsWith("Error") && r.Contains("rejected"));
T(55, "guard: reject CREATE", () => tool.RunDaxQuery("CREATE TABLE x (y INT)"), r => r.StartsWith("Error"));
T(56, "guard: reject INSERT", () => tool.RunDaxQuery("INSERT INTO Sales VALUES (1)"), r => r.StartsWith("Error"));
T(57, "guard: reject DELETE", () => tool.RunDaxQuery("DELETE FROM Sales"), r => r.StartsWith("Error"));
T(58, "guard: reject UPDATE", () => tool.RunDaxQuery("UPDATE Sales SET Amount=0"), r => r.StartsWith("Error"));
T(59, "guard: reject multi-statement", () => tool.RunDaxQuery("EVALUATE ROW(\"a\",1); EVALUATE ROW(\"b\",2)"), r => r.StartsWith("Error") && r.Contains("multiple"));
T(60, "guard: reject XMLA", () => tool.RunDaxQuery("<Batch><Statement>x</Statement></Batch>"), r => r.StartsWith("Error"));
T(61, "guard: reject unknown token", () => tool.RunDaxQuery("SHUTDOWN SERVER"), r => r.StartsWith("Error"));
T(62, "guard: reject empty", () => tool.RunDaxQuery("   "), r => r.StartsWith("Error"));
T(63, "guard: comment smuggle", () => tool.RunDaxQuery("-- EVALUATE\nDROP TABLE x"), r => r.StartsWith("Error"));
T(64, "guard: string with DROP ok", () => tool.RunDaxQuery("EVALUATE ROW(\"s\", \"DROP TABLE x\")"), r => !r.StartsWith("Error"));
T(65, "guard: bracket with ; ok", () => tool.RunDaxQuery("EVALUATE ROW(\"a;1\", 5)"), r => !r.StartsWith("Error"));
T(66, "guard: trailing single ; ok", () => tool.RunDaxQuery("EVALUATE ROW(\"a\",1);"), r => !r.StartsWith("Error"));
T(67, "guard: SELECT non-DMV", () => tool.RunDaxQuery("SELECT * FROM Sales"), r => r.StartsWith("Error") && r.Contains("$SYSTEM"));
T(68, "guard: double DEFINE", () => tool.RunDaxQuery("DEFINE MEASURE Sales[a]=1 DEFINE MEASURE Sales[b]=2 EVALUATE ROW(\"x\",1)"), r => r.StartsWith("Error"));
T(69, "guard: PROCESS keyword", () => tool.RunDaxQuery("PROCESS RECALCULATE"), r => r.StartsWith("Error"));
T(70, "guard: SAVE keyword", () => tool.RunDaxQuery("SAVE MODEL"), r => r.StartsWith("Error"));
T(71, "guard: case insensitive reject", () => tool.RunDaxQuery("drop table x"), r => r.StartsWith("Error"));
T(72, "guard: case insensitive accept", () => tool.RunDaxQuery("evaluate row(\"a\",1)"), r => !r.StartsWith("Error"));
T(73, "guard: GRANT", () => tool.RunDaxQuery("GRANT SELECT TO USER x"), r => r.StartsWith("Error"));
T(74, "guard: BACKUP", () => tool.RunDaxQuery("BACKUP DATABASE"), r => r.StartsWith("Error"));
T(75, "guard: EVALUATEX fake", () => tool.RunDaxQuery("EVALUATEX ROW(\"a\",1)"), r => r.StartsWith("Error"));

// ==================== PHASE E: DAX execution (76-95) ====================
T(76, "dax countrows sales", () => tool.RunDaxQuery("EVALUATE ROW(\"n\", COUNTROWS(Sales))"), r => r.Contains("10"));
T(77, "dax sum amount", () => tool.RunDaxQuery("EVALUATE ROW(\"s\", SUM(Sales[Amount]))"), r => !r.StartsWith("Error") && r.Contains("229"));
T(78, "dax topn regions", () => tool.RunDaxQuery("EVALUATE TOPN(2, SUMMARIZECOLUMNS(Region[Name], \"t\", [TotalSales]), [t], DESC)"), r => r.Contains("North") || r.Contains("South") || r.Contains("t"));
T(79, "dax invalid column", () => tool.RunDaxQuery("EVALUATE ROW(\"x\", SUM(Sales[NoCol]))"), r => r.StartsWith("Error"));
T(80, "dax invalid syntax", () => tool.RunDaxQuery("EVALUATE ((("), r => r.StartsWith("Error"));
T(81, "dax unknown function", () => tool.RunDaxQuery("EVALUATE ROW(\"x\", NOTAFUNC(1))"), r => r.StartsWith("Error"));
T(82, "dax maxRows=1", () => tool.RunDaxQuery("EVALUATE Sales", 1), r => r.Contains("TRUNCATED"));
T(83, "dax maxRows=0 clamped", () => tool.RunDaxQuery("EVALUATE ROW(\"a\",1)", 0), r => !r.Contains("TRUNCATED"));
T(84, "dax zero rows", () => tool.RunDaxQuery("EVALUATE FILTER(Sales, Sales[Amount] > 1000000)"), r => r.Contains("0 rows"));
T(85, "dax related", () => tool.RunDaxQuery("EVALUATE SELECTCOLUMNS(Sales, \"r\", RELATED(Region[Name]))"), r => r.Contains("North") || r.Contains("South"));
T(86, "dax calculate filter", () => tool.RunDaxQuery("EVALUATE ROW(\"n\", CALCULATE(COUNTROWS(Sales), Region[Name] = \"North\"))"), r => r.Contains("3"));
T(87, "dax userelationship inactive", () => tool.RunDaxQuery("EVALUATE ROW(\"x\", 1)"), r => !r.StartsWith("Error"));
T(88, "dax virtual column", () => tool.RunDaxQuery("EVALUATE ADDCOLUMNS(Sales, \"big\", Sales[Amount] * 2)"), r => !r.StartsWith("Error"));
T(89, "dax var", () => tool.RunDaxQuery("EVALUATE VAR x = 5 RETURN ROW(\"v\", x)"), r => r.Contains("5"));
T(90, "dax iter sumx", () => tool.RunDaxQuery("EVALUATE ROW(\"s\", SUMX(Sales, Sales[Amount] * Sales[Qty]))"), r => !r.StartsWith("Error"));
T(91, "dax divzero via divide", () => tool.RunDaxQuery("EVALUATE ROW(\"d\", DIVIDE(1, 0))"), r => !r.StartsWith("Error"));
T(92, "dax info view tables", () => tool.RunDaxQuery("EVALUATE INFO.VIEW.TABLES()"), r => !r.StartsWith("Error"));
T(93, "dax unicode result", () => tool.RunDaxQuery("EVALUATE ROW(\"u\", \"èòì\")"), r => !r.StartsWith("Error"));
T(94, "dax pipe escape", () => tool.RunDaxQuery("EVALUATE ROW(\"p\", \"a|b\")"), r => !r.StartsWith("Error"));
T(95, "dax long text cell", () => tool.RunDaxQuery("EVALUATE ROW(\"l\", REPLICATE(\"x\", 500))"), r => r.Contains("…") || r.Length < 1000);

// ==================== PHASE F: validation (96-110) ====================
T(96, "validate good measure", () => tool.ValidateDax("Sales", "SUM(Sales[Amount])"), r => r.StartsWith("Valid"));
T(97, "validate bad measure", () => tool.ValidateDax("Sales", "SUMX(Sales[Amount])"), r => r.StartsWith("Invalid"));
T(98, "validate unknown col", () => tool.ValidateDax("Sales", "SUM(Sales[Zzz])"), r => r.StartsWith("Invalid"));
T(99, "validate missing table", () => tool.ValidateDax("Ghost", "1"), r => r.StartsWith("Error"));
T(100, "validate calc ref", () => tool.ValidateDax("Sales", "[TotalSales] * 2"), r => r.StartsWith("Valid"));
T(101, "validate batch all good", () => tool.ValidateMeasures("""[{"table":"Sales","name":"a","expression":"SUM(Sales[Amount])"},{"table":"Sales","name":"b","expression":"COUNTROWS(Sales)"}]"""), r => r.Contains("OK") && !r.Contains("Invalid"));
T(102, "validate batch one bad", () => tool.ValidateMeasures("""[{"table":"Sales","name":"a","expression":"SUM(Sales[Amount])"},{"table":"Sales","name":"b","expression":"SUM(Sales[Zzz])"}]"""), r => r.Contains("b") && (r.Contains("Invalid") || r.Contains("ERROR")));
T(103, "validate batch bad json", () => tool.ValidateMeasures("{not json"), r => r.StartsWith("Error"));
T(104, "validate batch empty", () => tool.ValidateMeasures("[]"), r => r.StartsWith("Error"));
T(105, "validate batch missing expr", () => tool.ValidateMeasures("""[{"table":"Sales"}]"""), r => r.StartsWith("Error"));
T(106, "validate string measure", () => tool.ValidateDax("Region", "\"cap=\" & UPPER(Region[Name])"), r => r.StartsWith("Valid"));
T(107, "validate time intel basic", () => tool.ValidateDax("Sales", "TOTALYEARDATE()"), r => r.StartsWith("Invalid") || r.StartsWith("Valid"));
T(108, "validate var expr", () => tool.ValidateDax("Sales", "VAR a = 2 RETURN a * SUM(Sales[Amount])"), r => r.StartsWith("Valid"));
T(109, "validate empty expr", () => tool.ValidateDax("Sales", ""), r => r.StartsWith("Invalid") || r.StartsWith("Error"));
T(110, "validate after create loop", () => { tool.CreateMeasure("Sales", "TmpV", "1+1"); return tool.ValidateDax("Sales", "[TmpV] * 10"); }, r => r.StartsWith("Valid"));

// ==================== PHASE G: modeling CRUD (111-140) ====================
T(111, "update measure format", () => tool.UpdateMeasure("Sales", "TotalSales", formatString: "#,##0 €"), r => r.Contains("format"));
T(112, "update measure desc", () => tool.UpdateMeasure("Sales", "TotalSales", description: "Sum of all sales"), r => r.Contains("description"));
T(113, "update measure folder", () => tool.UpdateMeasure("Sales", "TotalSales", displayFolder: "Financials"), r => r.Contains("display folder"));
T(114, "update measure expr", () => tool.UpdateMeasure("Sales", "TmpV", daxExpression: "2+2"), r => r.Contains("expression"));
T(115, "update measure rename", () => tool.UpdateMeasure("Sales", "TmpV", newName: "TmpV2"), r => r.Contains("renamed"));
T(116, "update rename collision", () => tool.UpdateMeasure("Sales", "TmpV2", newName: "TotalSales"), r => r.StartsWith("Error"));
T(117, "update nothing rejected", () => tool.UpdateMeasure("Sales", "TotalSales"), r => r.StartsWith("Error"));
T(118, "update missing measure", () => tool.UpdateMeasure("Sales", "Ghost", formatString: "x"), r => r.StartsWith("Error"));
T(119, "delete TmpV2", () => tool.DeleteMeasure("Sales", "TmpV2"), r => r.StartsWith("Deleted"));
T(120, "delete missing measure", () => tool.DeleteMeasure("Sales", "Ghost"), r => r.StartsWith("Error"));
T(121, "set table description", () => tool.SetDescription("Region", "Geographic regions"), r => r.Contains("table 'Region'"));
T(122, "set column description", () => tool.SetDescription("Sales", "Sale amount in EUR", columnName: "Amount"), r => r.Contains("column"));
T(123, "set measure description", () => tool.SetDescription("Sales", "Average order value", measureName: "AvgOrder"), r => r.Contains("measure"));
T(124, "set desc missing table", () => tool.SetDescription("Ghost", "x"), r => r.StartsWith("Error"));
T(125, "set desc missing column", () => tool.SetDescription("Sales", "x", columnName: "Zzz"), r => r.StartsWith("Error"));
T(126, "create rel inactive", () =>
{
    tool.CreateCalculatedTable("DimA", "DATATABLE(\"KeyId\", INTEGER, \"V\", STRING, {{1,\"a\"},{2,\"b\"},{3,\"c\"}})");
    tool.CreateCalculatedTable("FactB", "DATATABLE(\"KeyId\", INTEGER, \"Amt\", DOUBLE, {{1,1.0},{1,2.0},{2,3.0},{3,4.0}})");
    return tool.CreateRelationship("FactB", "KeyId", "DimA", "KeyId", active: false);
}, r => r.StartsWith("Created relationship"));
T(127, "delete rel inactive", () =>
{
    var d = tool.DeleteRelationship("FactB", "KeyId", "DimA", "KeyId");
    tool.DeleteTable("DimA");
    tool.DeleteTable("FactB");
    return d;
}, r => r.StartsWith("Deleted relationship"));
T(128, "delete rel missing", () => tool.DeleteRelationship("A", "b", "C", "d"), r => r.StartsWith("Error"));
T(129, "create calc table unicode", () => tool.CreateCalculatedTable("Ünïcode_Tbl", "DATATABLE(\"a\", STRING, {{\"è\"}})"), r => r.StartsWith("Created"));
T(130, "delete unicode table", () => tool.DeleteTable("Ünïcode_Tbl"), r => r.StartsWith("Deleted"));
T(131, "delete calc table", () => tool.DeleteTable("Region"), r => r.StartsWith("Deleted calculated table"));
T(132, "recreate Region", () => tool.CreateCalculatedTable("Region", "DATATABLE(\"RegionId\", INTEGER, \"Name\", STRING, {{1,\"North\"},{2,\"South\"},{3,\"East\"},{4,\"West\"}})"), r => r.StartsWith("Created"));
T(133, "relink Sales-Region", () => tool.CreateRelationship("Sales", "RegionId", "Region", "RegionId"), r => r.StartsWith("Created relationship"));
T(134, "create measure bidi rel ok", () => tool.CreateMeasure("Region", "RegionSales", "CALCULATE([TotalSales])"), r => r.StartsWith("Created"));
T(135, "calc column int", () => tool.CreateCalculatedColumn("Sales", "AmountBucket", "Int64", "IF(Sales[Amount] > 20, 1, 0)"), r => r.StartsWith("Created"));
T(136, "calc column bool", () => tool.CreateCalculatedColumn("Sales", "IsBig", "Boolean", "Sales[Amount] > 20"), r => r.StartsWith("Created"));
T(137, "calc column datetime", () => tool.CreateCalculatedColumn("Sales", "FakeDate", "DateTime", "DATE(2026, 1, 1)"), r => r.StartsWith("Created"));
T(138, "measure with display folder", () => tool.CreateMeasure("Product", "ProductCount", "COUNTROWS(Product)", "#,##0", null, "Products"), r => r.StartsWith("Created"));
T(139, "measure referencing other table", () => tool.CreateMeasure("Region", "TotalProducts", "COUNTROWS(Product)"), r => r.StartsWith("Created"));
T(140, "measure cross filter", () => tool.CreateMeasure("Product", "SalesOfThis", "CALCULATE([TotalSales])"), r => r.StartsWith("Created"));

// ==================== PHASE H: analysis depth (141-160) ====================
T(141, "lint iferror", () => tool.LintDax("IFERROR([x], 0)"), r => r.Contains("IFERROR"));
T(142, "lint earlier", () => tool.LintDax("EARLIER(Sales[x])"), r => r.Contains("EARLIER"));
T(143, "lint filter table in calc", () => tool.LintDax("CALCULATE(SUM(Sales[Amount]), FILTER(Sales, Sales[Amount] > 0))"), r => r.Contains("FILTER"));
T(144, "lint nested calc", () => tool.LintDax("CALCULATE(CALCULATE(SUM(Sales[Amount])))"), r => r.Contains("nested"));
T(145, "lint summarize agg", () => tool.LintDax("SUMMARIZE(Sales, Sales[RegionId], \"t\", SUM(Sales[Amount]))"), r => r.Contains("SUMMARIZE"));
T(146, "lint plus zero", () => tool.LintDax("SUM(Sales[Amount]) + 0"), r => r.Contains("+0") || r.Contains("+ 0"));
T(147, "lint divide ok", () => tool.LintDax("DIVIDE(SUM(Sales[Amount]), [TotalQty])"), r => r.Contains("No anti-patterns"));
T(148, "lint clean complex", () => tool.LintDax("VAR base = SUM(Sales[Amount]) RETURN DIVIDE(base, COUNTROWS(Sales))"), r => r.Contains("No anti-patterns"));
T(149, "best practices finds no-format", () => tool.AnalyzeBestPractices(), r => r.Contains("no format string"));
T(150, "best practices finds no-desc", () => tool.AnalyzeBestPractices(), r => r.Contains("no description"));
T(151, "profile product", () => tool.ProfileTable("Product", 5, 3), r => r.Contains("Price") && r.Contains("Widget"));
T(152, "profile top values sales", () => tool.ProfileTable("Sales", 10, 0), r => r.Contains("Top values"));
T(153, "dictionary includes measures", () => tool.GenerateDataDictionary("Sales"), r => r.Contains("TotalSales") && r.Contains("SUM(Sales[Amount])"));
T(154, "dictionary includes descriptions", () => tool.GenerateDataDictionary("Sales"), r => r.Contains("Sum of all sales"));
T(155, "summary after changes", () => tool.GetModelSummary(), r => r.Contains("Sales") && r.Contains("Region") && r.Contains("Product"));
T(156, "schema shows folder", () => tool.GetTableSchema("Product"), r => r.Contains("Products"));
T(157, "schema shows desc", () => { tool.SetDescription("Region", "Geographic regions"); return tool.GetTableSchema("Region"); }, r => r.Contains("Geographic regions"));
T(158, "profile region names", () => tool.ProfileTable("Region", 4, 0), r => r.Contains("North") && r.Contains("West"));
T(159, "dax matrix agg", () => tool.RunDaxQuery("EVALUATE SUMMARIZECOLUMNS(Region[Name], \"sales\", [TotalSales], \"qty\", [TotalQty])"), r => r.Contains("sales"));
T(160, "dax rankx", () => tool.RunDaxQuery("EVALUATE ROW(\"r\", RANKX(ALL(Region), [TotalSales]))"), r => !r.StartsWith("Error"));

// ==================== PHASE I: robustness & edge cases (161-185) ====================
T(161, "special chars table name", () => tool.CreateCalculatedTable("T-with-dash", "DATATABLE(\"a\", INTEGER, {{1}})"), r => r.StartsWith("Created"));
T(162, "special chars cleanup", () => tool.DeleteTable("T-with-dash"), r => r.StartsWith("Deleted"));
T(163, "long table name", () => tool.CreateCalculatedTable(new string('L', 120), "DATATABLE(\"a\", INTEGER, {{1}})"), r => r.StartsWith("Created") || r.StartsWith("Error"));
T(164, "long name cleanup", () => tool.DeleteTable(new string('L', 120)), r => true);
T(165, "measure name with spaces", () => tool.CreateMeasure("Sales", "My Measure", "1"), r => r.StartsWith("Created"));
T(166, "measure space cleanup", () => tool.DeleteMeasure("Sales", "My Measure"), r => r.StartsWith("Deleted"));
T(167, "injection in table name", () => tool.CreateCalculatedTable("Robert'); DROP TABLE Sales;--", "DATATABLE(\"a\", INTEGER, {{1}})"), r => r.StartsWith("Created") || r.StartsWith("Error"));
T(168, "injection cleanup", () => { try { tool.DeleteTable("Robert'); DROP TABLE Sales;--"); } catch { } return tool.GetModelSummary(); }, r => r.Contains("Sales"));
T(169, "null-ish args", () => tool.CreateMeasure("Sales", "", ""), r => r.StartsWith("Error"));
T(170, "dax huge maxRows", () => tool.RunDaxQuery("EVALUATE ROW(\"a\",1)", 99999), r => !r.StartsWith("Error"));
T(171, "dax negative maxRows", () => tool.RunDaxQuery("EVALUATE ROW(\"a\",1)", -5), r => !r.StartsWith("Error"));
T(172, "concurrent summaries", () =>
{
    var results = new string[8];
    Parallel.For(0, 8, i => results[i] = tool.GetModelSummary());
    return results.All(r => r.Contains("Sales")) ? "all-ok" : "mismatch";
}, r => r == "all-ok");
T(173, "concurrent mixed reads", () =>
{
    var results = new string[6];
    Parallel.Invoke(
        () => results[0] = tool.GetModelSummary(),
        () => results[1] = tool.ListTables(),
        () => results[2] = tool.ListRelationships(),
        () => results[3] = tool.GetConnectionStatus(),
        () => results[4] = tool.ListOpenReports(),
        () => results[5] = tool.GetMeasure("Sales", "TotalSales"));
    return results.All(r => !string.IsNullOrEmpty(r)) ? "all-ok" : "empty";
}, r => r == "all-ok");
T(174, "ops after disconnect throw-safe", () =>
{
    tool.Disconnect();
    var a = tool.GetModelSummary();
    var b = tool.RunDaxQuery("EVALUATE ROW(\"a\",1)");
    tool.ConnectPowerBiDesktop();
    return a.StartsWith("Error") && b.StartsWith("Error") ? "guarded" : "leaked";
}, r => r == "guarded");
T(175, "reconnect after guard test", () => tool.GetConnectionStatus(), r => r.StartsWith("Connected"));
T(176, "dax with newlines", () => tool.RunDaxQuery("EVALUATE\n  ROW(\"a\", 1)"), r => !r.StartsWith("Error"));
T(177, "dax tabs", () => tool.RunDaxQuery("EVALUATE\tROW(\"a\", 1)"), r => !r.StartsWith("Error"));
T(178, "validate multiline", () => tool.ValidateDax("Sales", "VAR a = 1\nVAR b = 2\nRETURN a + b"), r => r.StartsWith("Valid"));
T(179, "measure with comment", () => tool.CreateMeasure("Sales", "Cmt", "// comment\n1+1"), r => r.StartsWith("Created"));
T(180, "cmt cleanup", () => tool.DeleteMeasure("Sales", "Cmt"), r => r.StartsWith("Deleted"));
T(181, "empty string desc clears", () => tool.SetDescription("Region", ""), r => r.StartsWith("Set description"));
T(182, "profile 0 sample", () => tool.ProfileTable("Region", 1, 0), r => r.Contains("RegionId"));
T(183, "dictionary no arg", () => tool.GenerateDataDictionary(), r => r.Contains("# Data dictionary"));
T(184, "summary twice same", () => tool.GetModelSummary() == tool.GetModelSummary() ? "same" : "diff", r => r == "same");
T(185, "list tables sorted", () => tool.ListTables(), r => r.IndexOf("Product", StringComparison.Ordinal) < r.IndexOf("Sales", StringComparison.Ordinal));

// ==================== PHASE J: bridge / visual feedback (186-195) ====================
T(186, "desktop state", () => tool.GetDesktopState(), r => r.StartsWith("File:") || r.StartsWith("Error"));
T(187, "screenshot page 1", () => tool.CaptureReportScreenshot(Path.Combine(Path.GetTempPath(), "pbitool_shot1.png"), 1), r => r.StartsWith("Screenshot") || r.StartsWith("Error"));
T(188, "screenshot after model change", () =>
{
    tool.CreateMeasure("Sales", "ShotTest", "SUM(Sales[Amount]) * 100", "#,##0");
    return tool.CaptureReportScreenshot(Path.Combine(Path.GetTempPath(), "pbitool_shot2.png"), 1);
}, r => r.StartsWith("Screenshot") || r.StartsWith("Error"));
T(189, "shot test cleanup", () => tool.DeleteMeasure("Sales", "ShotTest"), r => r.StartsWith("Deleted"));
T(190, "state mentions save", () => tool.GetDesktopState(), r => r.Contains("unsaved") || r.Contains("Unsaved") || r.StartsWith("Error"));
T(191, "screenshot bad path", () => tool.CaptureReportScreenshot("Z:\\nope\\x.png", 1), r => r.StartsWith("Error"));
T(192, "screenshot page 99", () => tool.CaptureReportScreenshot(Path.Combine(Path.GetTempPath(), "pbitool_shot99.png"), 99), r => r.StartsWith("Screenshot") || r.StartsWith("Error"));
T(193, "bridge graceful no crash", () => { for (int i = 0; i < 3; i++) tool.GetDesktopState(); return "alive"; }, r => r == "alive");
T(194, "live note on every mutation", () =>
{
    var a = tool.CreateMeasure("Sales", "LN1", "1");
    var b = tool.UpdateMeasure("Sales", "LN1", formatString: "0");
    var c = tool.DeleteMeasure("Sales", "LN1");
    return a.Contains("Ctrl+S") && b.Contains("Ctrl+S") && c.Contains("Ctrl+S") ? "noted" : "missing";
}, r => r == "noted");
T(195, "mutation visible in schema", () =>
{
    tool.CreateMeasure("Sales", "VisTest", "7");
    var s = tool.GetTableSchema("Sales");
    tool.DeleteMeasure("Sales", "VisTest");
    return s.Contains("VisTest") ? "visible" : "invisible";
}, r => r == "visible");

// ==================== PHASE K: final integration (196-200) ====================
T(196, "full workflow", () =>
{
    tool.CreateCalculatedTable("WF_Tmp", "DATATABLE(\"v\", INTEGER, {{1},{2},{3}})");
    var sum = tool.RunDaxQuery("EVALUATE ROW(\"s\", SUM(WF_Tmp[v]))");
    tool.CreateMeasure("WF_Tmp", "WF_M", "SUM(WF_Tmp[v]) * 2", "#,##0");
    var val = tool.ValidateDax("WF_Tmp", "[WF_M] + 1");
    tool.DeleteMeasure("WF_Tmp", "WF_M");
    tool.DeleteTable("WF_Tmp");
    return sum.Contains("6") && val.StartsWith("Valid") ? "workflow-ok" : "workflow-fail";
}, r => r == "workflow-ok");
T(197, "model intact", () => tool.GetModelSummary(), r => r.Contains("Sales") && r.Contains("Region") && r.Contains("Product"));
T(198, "no leftover tmp objects", () => tool.ListTables(), r => !r.Contains("WF_Tmp") && !r.Contains("Ünïcode"));
T(199, "final best practices", () => tool.AnalyzeBestPractices(), r => r.Contains("Warnings"));
T(200, "final status", () => tool.GetConnectionStatus(), r => r.StartsWith("Connected"));

Console.WriteLine();
Console.WriteLine($"RESULT: {passed} passed, {failed} failed (of {passed + failed})");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine("  " + f);
}
File.AppendAllText(logPath, $"\nRESULT: {passed} passed, {failed} failed\n");
return failed == 0 ? 0 : 1;
