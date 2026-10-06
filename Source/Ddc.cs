using System.Runtime.InteropServices;
using System.Text;

namespace MonitorSourceSwitcher;

/// <summary>
/// Minimal DDC/CI client built on dxva2.dll, a stock Windows system DLL.
/// No third-party dependency and no vendor SDK: the low-level monitor configuration
/// API is part of Windows itself.
/// </summary>
public static class Ddc
{
    /// <summary>MCCS VCP code 0x00 - Capabilities String, which lists the VCP codes a panel implements.</summary>
    public const byte VcpCapabilities = 0x00;

    /// <summary>MCCS VCP code 0x10 - Luminance. Used as the cheapest "does this link answer?" probe.</summary>
    public const byte VcpBrightness = 0x10;

    /// <summary>MCCS VCP code 0x60 - Input Select.</summary>
    public const byte VcpInputSelect = 0x60;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcp, out uint type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcp, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

    [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] StringBuilder reply, uint length);

    public sealed class Monitor
    {
        internal IntPtr Handle { get; init; }
        public int Id { get; init; }
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Owns the native physical-monitor handles. Dispose it or the handles leak.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private readonly List<(uint Count, PHYSICAL_MONITOR[] Handles)> _owned = new();

        public List<Monitor> Monitors { get; } = new();

        public static Session Open()
        {
            var session = new Session();

            var hmons = new List<IntPtr>();
            MonitorEnumProc callback = (h, hdc, rect, data) => { hmons.Add(h); return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            var id = 0;
            foreach (var hmon in hmons)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var count) || count == 0) continue;

                var handles = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, handles)) continue;
                session._owned.Add((count, handles));

                foreach (var h in handles)
                {
                    id++;
                    session.Monitors.Add(new Monitor
                    {
                        Id = id,
                        Handle = h.hPhysicalMonitor,
                        Description = (h.szPhysicalMonitorDescription ?? "").Trim()
                    });
                }
            }

            return session;
        }

        public Monitor? ById(int id) => Monitors.FirstOrDefault(m => m.Id == id);

        /// <summary>
        /// Reads a VCP code. Returns false when the monitor does not answer - the normal result
        /// on a link that carries no DDC/CI at all, and for a code a panel does not implement.
        /// </summary>
        public bool TryGetVcp(Monitor monitor, byte vcp, out uint current, out uint max)
        {
            current = 0;
            max = 0;

            if (!GetVCPFeatureAndVCPFeatureReply(monitor.Handle, vcp, out _, out var cur, out var mx))
                return false;

            current = cur;
            max = mx;

            // A zero value has proven to mean "no answer" on every panel tested; a monitor
            // reporting a feature it supports does not return 0 for a live reading.
            return cur != 0;
        }

        /// <summary>
        /// Sends a VCP write. The return value is the panel's acknowledgement: it comes back TRUE
        /// for a code the panel implements and FALSE for one it does not, which is what makes a
        /// false result here mean "this did not happen" rather than "no idea".
        /// </summary>
        public bool TrySetVcp(Monitor monitor, byte vcp, uint value)
            => SetVCPFeature(monitor.Handle, vcp, value);

        /// <summary>Returns the panel's raw MCCS capabilities string, or false if it will not provide one.</summary>
        public bool TryGetCapabilities(Monitor monitor, out string capabilities)
        {
            capabilities = "";

            if (!GetCapabilitiesStringLength(monitor.Handle, out var length) || length == 0)
                return false;

            var buffer = new StringBuilder((int)length);
            if (!CapabilitiesRequestAndCapabilitiesReply(monitor.Handle, buffer, length))
                return false;

            capabilities = buffer.ToString();
            return capabilities.Length > 0;
        }

        public void Dispose()
        {
            foreach (var (count, handles) in _owned)
                DestroyPhysicalMonitors(count, handles);

            _owned.Clear();
            Monitors.Clear();
        }
    }
}
