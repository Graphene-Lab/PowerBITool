# PowerBITool

A Power BI Desktop agent tool for [AgentBridge](https://github.com/Graphene-Lab/AgentBridge) / AIOrchestrator.

It lets an AI agent connect to the **local** Analysis Services instance (`msmdsrv.exe`) that Power BI Desktop
starts for each open report, then read and edit the semantic model: tables, calculated tables and columns,
measures, relationships. It runs and validates DAX, profiles data, builds a data dictionary, lints best
practices, and can capture report screenshots through the Power BI Desktop Bridge.

**Local only.** It talks to the Power BI Desktop running on your own machine. It does not use the Microsoft
cloud, Power BI Service, or Fabric.

## What it can do

- **Connect** to the running Power BI Desktop model (auto-discovers the local engine port).
- **Inspect**: model summary, table schema, list tables / measures / relationships, get a measure.
- **Edit the model**: create/delete calculated tables and columns, create/update/delete measures, create/delete
  relationships, set descriptions.
- **Run DAX** (read-only): `EVALUATE` queries and `$SYSTEM.*` DMVs, with a fail-closed guard that rejects
  anything that could modify the model.
- **Validate DAX** before creating a measure, one at a time or in batch.
- **Profile** a table (distinct / blanks / min / max / top values / sample rows).
- **Document**: generate a data dictionary; lint DAX and the model for best-practice issues.
- **See the result**: capture a report page screenshot through the Desktop Bridge.

## How it works

```
You  →  AgentBridge  →  AIOrchestrator  →  PowerBITool  →  local msmdsrv (Power BI Desktop)
```

PowerBITool is a plugin. It is loaded by the host (AgentBridge / AIOrchestrator) from the `Tools/` folder.
No plugin code is compiled into the host: the agent calls tools by name.

## Prerequisites

- **Windows.** Power BI Desktop is Windows-only, so this tool works only on Windows.
- **Power BI Desktop** installed and a report open.
  - For model editing and DAX: any recent Power BI Desktop.
  - For screenshots through the Desktop Bridge: a June 2026 or newer build with the preview
    *"Enable external tool access to Power BI Desktop through secure local APIs"* turned on.
- **AgentBridge** (the host that runs the agent).
- **.NET 10 runtime**.

## Install

### Option A — installer script (recommended)

From a PowerShell window in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

The installer:

1. Checks for **AgentBridge**. If it is not installed, it offers to download and install it
   (from the official GitHub release).
2. Installs the **PowerBITool plugin** into AgentBridge's `Tools\PowerBITool\` folder.
3. Checks for **Power BI Desktop**. If it is missing, it asks whether you want to install it; on "yes" it
   opens your default browser to the official download page.

Useful options:

```powershell
# Point at an existing AgentBridge install instead of auto-detecting
powershell -ExecutionPolicy Bypass -File install.ps1 -AgentBridgeDir "C:\path\to\AgentBridge"

# Skip the AgentBridge auto-install (fail if it is missing)
powershell -ExecutionPolicy Bypass -File install.ps1 -NoAgentBridgeInstall
```

### Option B — manual

Copy the built plugin files into AgentBridge's plugin folder:

```
<AgentBridge install>\Tools\PowerBITool\
```

The host finds it at startup, or adds it while the app is running. The folder contains only the plugin and
its own dependencies; the AIOrchestrator libraries come from the host.

## Using it

Start AgentBridge, open a report in Power BI Desktop, then ask the agent to work on the model — for example:
"connect to Power BI", "list the tables", "create a measure Total Sales = SUM(Sales[Amount])",
"validate this DAX", "profile the Sales table".

## Persistence

Every change is applied to the **live in-memory model** in Power BI Desktop and is visible immediately.
It is **not** written to the `.pbix` file automatically. To keep your changes, press **Ctrl+S** in
Power BI Desktop. The tool reminds you of this after each change.

## Verification

The tool was validated with 200 real-usage tests run against a live Power BI Desktop instance
(connection lifecycle, model editing, DAX guard, validation, profiling, screenshots, concurrency,
edge cases). All 200 pass.

## License

See [LICENSE.md](LICENSE.md).
