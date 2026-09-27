using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AIOrchestrator;

namespace PowerBIToolNS;

/// <summary>
/// Client for the Power BI Desktop Bridge: a JSON-RPC 2.0 server that runs INSIDE
/// PBIDesktop.exe (June 2026+ builds) and listens on the named pipe
/// \\.\pipe\pbi-desktop-bridge-&lt;desktopPid&gt;, with Content-Length framed messages.
/// Requires the Power BI Desktop preview option "Enable external tool access to
/// Power BI Desktop through secure local APIs" (on by default).
///
/// This is the visual-feedback channel: the agent can capture a PNG screenshot of a
/// report page as rendered by Power BI Desktop right after its changes, and read it
/// back to verify what the user sees. The bridge allows ONE in-flight operation at a
/// time, so every call is serialized with a lock and a timeout.
/// </summary>
internal static class DesktopBridge
{
    private static readonly object BridgeLock = new();

    internal sealed record BridgeResult(bool Ok, JsonElement? Result, string? Error);

    /// <summary>Sends one JSON-RPC call to the Desktop Bridge of the given PBIDesktop PID.</summary>
    internal static BridgeResult Call(int desktopPid, string method, Dictionary<string, object?>? args = null, int timeoutMs = 30000)
    {
        if (desktopPid <= 0)
            return new BridgeResult(false, null, "no Power BI Desktop process id known — reconnect with ConnectPowerBiDesktop.");

        lock (BridgeLock)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", $"pbi-desktop-bridge-{desktopPid}",
                    PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(timeoutMs);

                var payload = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = method,
                };
                if (args != null) payload["params"] = args;
                string body = JsonSerializer.Serialize(payload);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

                byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
                pipe.Write(header, 0, header.Length);
                pipe.Write(bodyBytes, 0, bodyBytes.Length);
                pipe.Flush();

                string response = ReadFramed(pipe, timeoutMs);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                {
                    string msg = err.TryGetProperty("message", out var em) ? em.GetString() ?? "unknown" : err.ToString();
                    return new BridgeResult(false, null, msg);
                }
                var result = root.GetProperty("result").Clone();
                return new BridgeResult(true, result, null);
            }
            catch (TimeoutException)
            {
                return new BridgeResult(false, null, $"Desktop Bridge pipe timeout (is the preview option 'Enable external tool access to Power BI Desktop through secure local APIs' enabled, and the report fully loaded?)");
            }
            catch (Exception ex)
            {
                return new BridgeResult(false, null, $"Desktop Bridge unavailable: {ex.Message}");
            }
        }
    }

    /// <summary>Reads a Content-Length framed response from the pipe.</summary>
    private static string ReadFramed(Stream pipe, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        // Read header lines until the blank line, then the body by Content-Length.
        int contentLength = -1;
        string line;
        while ((line = ReadLine(pipe)) != null)
        {
            if (line.Length == 0) break; // end of headers
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
        }
        if (contentLength < 0) throw new IOException("No Content-Length in bridge response");
        byte[] body = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = pipe.Read(body, read, contentLength - read);
            if (n <= 0) break;
            read += n;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
        }
        return Encoding.UTF8.GetString(body, 0, read);
    }

    private static string? ReadLine(Stream pipe)
    {
        var sb = new StringBuilder();
        int prev = -1, cur;
        while ((cur = pipe.ReadByte()) >= 0)
        {
            if (prev == '\r' && cur == '\n')
            {
                if (sb.Length > 0) sb.Length -= 1; // drop the \r
                return sb.ToString();
            }
            sb.Append((char)cur);
            prev = cur;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}

/// <summary>
/// Agent-facing visual-feedback operations over the Power BI Desktop Bridge:
/// live screenshot of a report page (the agent can look at its own work),
/// application state (current file, unsaved-changes flag) and hot reload.
/// </summary>
public partial class PowerBITool
{
    /// <summary>
    /// Captures a PNG screenshot of a report page AS RENDERED in the connected
    /// Power BI Desktop window — the visual feedback loop: after creating measures or
    /// changing the model, take a screenshot to see the report state, and use it to
    /// verify and correct your own work. Requires the Power BI Desktop preview option
    /// "Enable external tool access to Power BI Desktop through secure local APIs"
    /// (on by default in June 2026+ builds).
    /// </summary>
    /// <param name="outputPath">File path (PNG) where to save the screenshot.</param>
    /// <param name="pageNumber">1-based page index to capture (default 1).</param>
    /// <returns>Confirmation with the saved PNG path, or "Error: ...".</returns>
    public string CaptureReportScreenshot(string outputPath, int pageNumber = 1)
    {
        try
        {
            lock (ConnLock)
            {
                RequireDb();
                if (_desktopPid <= 0)
                    return "Error: Power BI Desktop window pid unknown — reconnect with ConnectPowerBiDesktop.";

                // The bridge manifest marks pageId and scale as required keys: scale is
                // nullable but the key MUST be present or Desktop null-refs.
                var res = DesktopBridge.Call(_desktopPid, "report.snapshot.capture/v1",
                    new Dictionary<string, object?> { ["pageNumber"] = pageNumber, ["scale"] = null }, 60000);
                if (!res.Ok)
                    return $"Error: screenshot failed — {res.Error}";

                if (res.Result!.Value.TryGetProperty("imageBase64", out var img))
                {
                    File.WriteAllBytes(outputPath, Convert.FromBase64String(img.GetString()!));
                    Log.LogStep($"PowerBITool: screenshot saved to {outputPath}");
                    return $"Screenshot of page {pageNumber} saved to {outputPath}. Read the PNG to see what Power BI Desktop shows.";
                }
                // Some builds return the image as a file path instead of base64.
                if (res.Result.Value.TryGetProperty("path", out var p))
                    return $"Screenshot saved by Power BI Desktop to {p.GetString()}.";
                return "Error: bridge returned no image payload. Raw: " + res.Result.Value.ToString();
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Reads the Power BI Desktop application state: the file currently open and
    /// whether it has unsaved changes. Use before advising the user to save, and to
    /// confirm which file the live model belongs to.
    /// </summary>
    /// <returns>"File: ... | Unsaved changes: yes/no" or "Error: ...".</returns>
    public string GetDesktopState()
    {
        try
        {
            lock (ConnLock)
            {
                RequireDb();
                if (_desktopPid <= 0)
                    return "Error: Power BI Desktop window pid unknown — reconnect with ConnectPowerBiDesktop.";
                var res = DesktopBridge.Call(_desktopPid, "application.state.get/v1", null, 15000);
                if (!res.Ok) return $"Error: {res.Error}";
                var v = res.Result!.Value;
                string file = v.TryGetProperty("currentFilePath", out var f) ? f.GetString() ?? "?" : "?";
                bool unsaved = v.TryGetProperty("hasUnsavedChanges", out var u) && u.GetBoolean();
                return $"File: {file} | Unsaved changes: {(unsaved ? "yes — remind the user to press Ctrl+S" : "no")}";
            }
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
