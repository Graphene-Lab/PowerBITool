using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PowerBIToolNS;

/// <summary>
/// Discovers the local Analysis Services instance (msmdsrv.exe) that Microsoft Power BI
/// Desktop spawns for each open .pbix window, and opens the AMO connection to it.
///
/// Discovery model (same approach as DAX Studio / Tabular Editor / the community
/// Power BI MCP servers): Power BI Desktop (PBIDesktop.exe) launches one msmdsrv.exe
/// child per open report; each msmdsrv listens on a dynamic TCP port on localhost.
///
/// Two candidate sources, merged and de-duplicated:
///  1. Process scan: msmdsrv.exe processes that are children of a PBIDesktop.exe,
///     with their listening ports read from the kernel TCP table.
///  2. Port files: %LOCALAPPDATA%\Microsoft\Power BI Desktop[\ Store App]\
///     AnalysisServicesWorkspaces\*\Data\msmdsrv.port.txt — these SURVIVE Desktop
///     closing, so every candidate port gets a fast TCP pre-probe before any AMO
///     connect, and the AMO probe (which reads the live database name) is the final
///     authority. A port that answers is verified, never assumed.
///
/// Everything stays on localhost — no cloud, no Fabric.
/// </summary>
internal static class PowerBiDesktopLocator
{
    /// <summary>One discovered Power BI Desktop instance: the msmdsrv PID, the owning
    /// PBIDesktop PID (for the Desktop Bridge named pipe), the TCP port and the live
    /// model (database) name read during the probe connect.</summary>
    internal sealed record DiscoveredInstance(int EnginePid, int DesktopPid, int Port, string DatabaseName, DateTime EngineStart);

    /// <summary>Finds every msmdsrv instance spawned by a running PBIDesktop.exe and
    /// probes each for its live database name. Instances that answer are returned;
    /// ports that do not speak Analysis Services are skipped silently.</summary>
    internal static List<DiscoveredInstance> FindInstances()
    {
        var result = new List<DiscoveredInstance>();
        var desktopPids = new List<int>();
        try
        {
            desktopPids = Process.GetProcessesByName("PBIDesktop").Select(p => p.Id).ToList();
        }
        catch { }

        var candidates = new List<(int EnginePid, int Port, DateTime Start)>();

        // Source 1: msmdsrv children of a live PBIDesktop.
        if (desktopPids.Count > 0)
        {
            try
            {
                foreach (var ms in Process.GetProcessesByName("msmdsrv"))
                {
                    try
                    {
                        int? parent = GetParentProcessId(ms.Id);
                        if (parent == null || !desktopPids.Contains(parent.Value)) continue;
                        foreach (var port in ListeningPortsOf(ms.Id))
                            candidates.Add((ms.Id, port, SafeStart(ms)));
                    }
                    catch { }
                    finally { ms.Dispose(); }
                }
            }
            catch { }
        }

        // Source 2: port files from the Analysis Services workspaces (MSI + Store layouts).
        foreach (var portFile in PortFiles())
        {
            int port = ReadPortFile(portFile);
            if (port <= 0) continue;
            if (candidates.Any(c => c.Port == port)) continue;
            // Orphan files: attribute to the engine that currently owns the port, if any.
            int owner = OwnerOfPort(port);
            if (owner > 0)
            {
                try
                {
                    using var ms = Process.GetProcessById(owner);
                    if (!string.Equals(ms.ProcessName, "msmdsrv", StringComparison.OrdinalIgnoreCase)) continue;
                    int? parent = GetParentProcessId(owner);
                    if (parent == null || !desktopPids.Contains(parent.Value)) continue;
                    candidates.Add((owner, port, SafeStart(ms)));
                }
                catch { }
            }
        }

        foreach (var (enginePid, port, start) in candidates.DistinctBy(c => c.Port))
        {
            // Fast TCP pre-probe (0.5 s) so a dead port never costs a full AMO timeout.
            if (!TcpAlive("127.0.0.1", port, 500)) continue;
            string? dbName = ProbeDatabaseName(port);
            if (dbName == null) continue;
            int desktopPid = GetParentProcessId(enginePid) ?? 0;
            result.Add(new DiscoveredInstance(enginePid, desktopPid, port, dbName, start));
        }
        return result;
    }

    private static DateTime SafeStart(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MinValue; }
    }

    // ---- port files ----

    private static IEnumerable<string> PortFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[]
        {
            Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            Path.Combine(local, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
        })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "msmdsrv.port.txt", SearchOption.AllDirectories);
            }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>Reads a port file tolerating its encoding traps: try UTF-16, UTF-8-BOM,
    /// UTF-8 and accept only a value that parses as a port in 1..65535 (an even-length
    /// UTF-8 buffer can decode as valid-but-garbage UTF-16).</summary>
    private static int ReadPortFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); } catch { return -1; }
        foreach (var enc in new[] { System.Text.Encoding.Unicode, new System.Text.UTF8Encoding(true), System.Text.Encoding.UTF8 })
        {
            int? port = TryParsePort(enc.GetString(bytes));
            if (port.HasValue) return port.Value;
        }
        return -1;
    }

    private static int? TryParsePort(string text)
    {
        text = text.Trim().Trim('\0');
        if (int.TryParse(text, out int port) && port is > 0 and <= 65535) return port;
        return null;
    }

    // ---- TCP helpers ----

    internal static bool TcpAlive(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect(host, port, null, null);
            return connect.AsyncWaitHandle.WaitOne(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    // ---- TCP table (listening ports with owning PID) via GetExtendedTcpTable ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningProcess;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutLen,
        bool sort, int addressFamily, int tableClass, int reserved);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 5;

    private static List<(int Pid, int Port)> ReadTable()
    {
        var rows = new List<(int, int)>();
        int length = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref length, true, AfInet, TcpTableOwnerPidListener, 0);
        if (ret != 122 && ret != 0) return rows; // 122 = ERROR_INSUFFICIENT_BUFFER
        IntPtr table = Marshal.AllocHGlobal(length);
        try
        {
            ret = GetExtendedTcpTable(table, ref length, true, AfInet, TcpTableOwnerPidListener, 0);
            if (ret != 0) return rows;
            int count = Marshal.ReadInt32(table);
            IntPtr rowPtr = table + 4;
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr + i * rowSize);
                // LocalPort is stored network-byte-order in the low 16 bits.
                int port = ((int)row.LocalPort & 0xFF) << 8 | ((int)row.LocalPort >> 8 & 0xFF);
                if (port > 0 && row.OwningProcess > 0)
                    rows.Add((row.OwningProcess, port));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
        return rows;
    }

    private static IEnumerable<int> ListeningPortsOf(int pid)
        => ReadTable().Where(r => r.Pid == pid).Select(r => r.Port).Distinct();

    private static int OwnerOfPort(int port)
        => ReadTable().FirstOrDefault(r => r.Port == port).Pid;

    // ---- AMO probe ----

    /// <summary>Reads the live database name over AMO on localhost:port. Returns null
    /// when the port is not an Analysis Services endpoint or has no database.</summary>
    private static string? ProbeDatabaseName(int port)
    {
        try
        {
            using var server = new Microsoft.AnalysisServices.Server();
            server.Connect($"localhost:{port}");
            if (server.Databases.Count == 0) return null;
            var name = server.Databases[0].Name;
            server.Disconnect();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    // ---- Parent PID via NtQueryInformationProcess ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle,
        int processInformationClass, ref ProcessBasicInformation processInformation,
        int processInformationLength, out int returnLength);

    internal static int? GetParentProcessId(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var info = new ProcessBasicInformation();
            int res = NtQueryInformationProcess(p.Handle, 0, ref info,
                Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (res != 0) return null;
            return (int)info.InheritedFromUniqueProcessId;
        }
        catch { return null; }
    }
}
