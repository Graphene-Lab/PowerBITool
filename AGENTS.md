# PowerBITool — notes for coding agents

> Read this file first. This repo is one tool plugin of the AIOffice solution: it is compiled on
> its own and loaded by the hosts from `Tools/PowerBITool/` at runtime.

## What this is

The semantic model of the **local** Power BI Desktop instance, reached through its Analysis
Services endpoint: tables, columns, measures, relationships, DAX run and validation, data
profiling, best-practice lint. Local only — no cloud, no Fabric.

## Read before you edit

| If you touch… | Read first |
|---|---|
| anything | [PLUGIN-GUARDRAILS](../AIOrchestrator/docs-dev/PLUGIN-GUARDRAILS.md) — the contract shared by the 12 plugins; the file lives in the sibling AIOrchestrator repo (private), reachable from the solution checkout |
| the connection to Power BI Desktop | `DesktopBridge.cs` and `README.md` |
| a tool method or its XML docs | [AGENT_TOOLS_GUIDE](../AIOrchestrator/API/AGENT_TOOLS_GUIDE.md) |

## Non-negotiables

1. The class derives from `BaseAgentTool` and keeps
   `[ToolPurpose(ToolPurpose.BusinessIntelligence)]`.
2. Public methods and their XML docs **are** the agent's surface: no public method without docs,
   and a public method is never renamed (the model calls it by that name).
3. AnyCPU, no `RuntimeIdentifier`, `GenerateDocumentationFile` stays on.
4. The plugin folder never carries `AIOrchestrator.dll`; no state is written into `Tools/`.
5. **Local only.** The tool talks to the running Power BI Desktop on the same machine; it never
   reaches a cloud service.
6. **No NuGet package, by design.** `PackageId` and the assembly metadata exist because
   `PluginUpdater` uses them as the plugin's runtime identity; the distribution channel is the
   GitHub release zip only. Do not add a `publish.yml` without deciding where the Windows-only
   dependency graph goes.
7. A new metadata goes in a class attribute (or a `virtual` member with a default) — never an
   `abstract` member: the dlls already shipped must keep loading.

## Where things are

- `PowerBITool.cs` — the tool class; its public methods are what the model can call
- `DesktopBridge.cs` — discovery and connection to the local Analysis Services instance
- `PowerBITool.Harness/` — nested harness, excluded from the library compile

## Shipping

Publishes a GitHub release zip only. AIOffice builds it into `Tools/PowerBITool` on every dev build
with its dependencies listed package by package in `AIOffice.csproj`; AgentBridge does not fetch it.
