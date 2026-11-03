// Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM - changes in this fork (see its git history).
// Per-process GPU utilisation monitor for the console GPU% indicator.
// Samples the Windows "GPU Engine" performance counter category (the same
// source Task Manager's per-process GPU column uses), summing every engine
// instance that belongs to our PID, and reports 0..100 on the UI thread.
//
// Engines (3D/Copy/VideoDecode/...) come and go dynamically, so instance
// enumeration is refreshed periodically on a worker thread; sampling is a
// plain NextValue() sum. If the category is missing (pre-WDDM2.0 driver,
// stripped-down system) the monitor reports unavailable exactly once and
// stays dormant - the UI hides the indicator in that case.
//
// Reported value = the busiest engine (as Task Manager), clamped to 100. That is a share of the
// time at the CURRENT clock; on an NVIDIA card (NVML from the driver) it is also scaled by
// current / max graphics clock, so a card idling along at 1/3 clock no longer reads 3x busier.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Thetis
{
    internal sealed class GpuUsageMonitor : IDisposable
    {
        private const string c_categoryName = "GPU Engine";
        private const string c_counterName = "Utilization Percentage";
        private const int c_enumIntervalMs = 5000;
        private const int c_sampleIntervalMs = 1000;

        private readonly string _instancePrefix;
        private readonly SynchronizationContext _uiContext;
        private readonly Action<string, string> _onSample; // label text + tooltip on UI thread, null text => unavailable

        private List<PerformanceCounter> _counters = new List<PerformanceCounter>();
        private Timer _sampleTimer;
        private Timer _enumTimer;
        private bool _started;
        private bool _unavailableReported;
        private int _disposed;

        public GpuUsageMonitor(Action<string, string> onSample)
        {
            _onSample = onSample ?? delegate { };
            _uiContext = SynchronizationContext.Current;
            _instancePrefix = "pid_" + Process.GetCurrentProcess().Id + "_";
        }

        public void Start()
        {
            if (_started)
                return;
            _started = true;

            // PerformanceCounterCategory.Exists can take seconds - keep off the UI thread.
            // The threadpool is used for all counter access from here on.
            Task.Run(() =>
            {
                try
                {
                    if (!PerformanceCounterCategory.Exists(c_categoryName))
                    {
                        reportUnavailable();
                        return;
                    }

                    if (!enumerateInstances())
                    {
                        reportUnavailable();
                        return;
                    }
                }
                catch
                {
                    reportUnavailable();
                    return;
                }

                _enumTimer = new Timer(delegate { enumerateInstances(); }, null, c_enumIntervalMs, c_enumIntervalMs);
                _sampleTimer = new Timer(delegate { sample(); }, null, c_sampleIntervalMs, c_sampleIntervalMs);
            });
        }

        // returns true when at least one matching engine instance was found
        private bool enumerateInstances()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            // counters of instances still present are kept: a new counter's first NextValue() is always 0
            // (rate counter, needs two reads), so recreating them all every refresh showed 0% every 5 s
            List<PerformanceCounter> current = Volatile.Read(ref _counters);
            Dictionary<string, PerformanceCounter> have = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
            foreach (PerformanceCounter pc in current) have[pc.InstanceName] = pc;
            List<PerformanceCounter> fresh = new List<PerformanceCounter>();
            List<PerformanceCounter> added = new List<PerformanceCounter>();
            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory(c_categoryName);
                foreach (string inst in cat.GetInstanceNames())
                {
                    if (!inst.StartsWith(_instancePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (have.TryGetValue(inst, out PerformanceCounter pc))
                    {
                        fresh.Add(pc);
                        have.Remove(inst);
                        continue;
                    }
                    pc = new PerformanceCounter(c_categoryName, c_counterName, inst, true);
                    try { pc.NextValue(); } catch { } // prime: the next sample reads a real value
                    added.Add(pc);
                    fresh.Add(pc);
                }
            }
            catch
            {
                // category vanished / transient PDH failure - keep whatever we had
                disposeList(added);
                return current.Count > 0;
            }

            if (fresh.Count == 0 && current.Count > 0)
            {
                // nothing matched this refresh (engines idle & deregistered);
                // keep previous counters so the next sample still reads ~0
                return true;
            }

            Interlocked.Exchange(ref _counters, fresh);
            disposeList(new List<PerformanceCounter>(have.Values)); // instances that are gone
            return fresh.Count > 0;
        }

        private void sample()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            // as Task Manager: per engine (3D, Copy, VideoDecode, ...) the sum of our instances on it, the
            // busiest engine is the GPU % - summing every engine overstated it
            Dictionary<string, float> perEngine = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            List<PerformanceCounter> snapshot = Volatile.Read(ref _counters);
            foreach (PerformanceCounter pc in snapshot)
            {
                try
                {
                    float v = pc.NextValue();
                    if (v <= 0f)
                        continue;
                    string key = engineKey(pc.InstanceName);
                    perEngine.TryGetValue(key, out float s);
                    perEngine[key] = s + v;
                }
                catch
                {
                    // instance died between enum and sample - ignore until next enum
                }
            }

            float max = 0f;
            string maxKey = null;
            foreach (KeyValuePair<string, float> kv in perEngine)
                if (kv.Value > max) { max = kv.Value; maxKey = kv.Key; }
            max = Math.Min(max, 100f);

            if (maxKey != null && Nvml.Read(luidOf(maxKey), out uint mhz, out uint maxMhz, out uint mw) && maxMhz > 0)
            {
                float real = max * mhz / maxMhz;
                post("GPU " + real.ToString(real < 10f ? "0.0" : "0") + "%",
                     "Thetis GPU load, scaled to the full clock: " + real.ToString("0.0") + "%\n" +
                     "busiest engine " + max.ToString("0.0") + "% at " + mhz + " of " + maxMhz + " MHz" +
                     (mw > 0 ? "\ncard power " + (mw / 1000.0).ToString("0.0") + " W" : ""));
            }
            else
                post("GPU " + Math.Round(max) + "%", "Thetis GPU load: busiest engine " + max.ToString("0.0") + "% at the current clock");
        }

        // "luid_0x00000000_0x0001101a_phys_0_eng_3" -> "luid_0x00000000_0x0001101a"
        private static string luidOf(string key)
        {
            int p = key.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            return p > 0 ? key.Substring(0, p) : key;
        }

        // NVIDIA management library from the driver; DXGI adapter LUID -> NVML device by PCI ids
        private static class Nvml
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            private struct PciInfo
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
                public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
            }
            [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int Init();
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint n);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint i, out IntPtr dev);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3")] private static extern int GetPci(IntPtr dev, out PciInfo pci);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr dev, int type, out uint mhz);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")] private static extern int GetMaxClock(IntPtr dev, int type, out uint mhz);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr dev, out uint mw);

            private static Dictionary<string, IntPtr> _byLuid;   // null = not tried yet, empty = none / no NVML
            private const int ClockGraphics = 0;

            public static bool Read(string luid, out uint mhz, out uint maxMhz, out uint mw)
            {
                mhz = maxMhz = mw = 0;
                if (_byLuid == null) _byLuid = map();
                if (!_byLuid.TryGetValue(luid, out IntPtr dev)) return false;
                try
                {
                    if (GetClock(dev, ClockGraphics, out mhz) != 0 || GetMaxClock(dev, ClockGraphics, out maxMhz) != 0) return false;
                    if (GetPower(dev, out mw) != 0) mw = 0;
                    return true;
                }
                catch { return false; }
            }

            private static Dictionary<string, IntPtr> map()
            {
                var m = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Init() != 0 || GetCount(out uint n) != 0) return m;
                    var devs = new List<(IntPtr dev, uint id)>();
                    for (uint i = 0; i < n; i++)
                        if (GetHandle(i, out IntPtr d) == 0 && GetPci(d, out PciInfo pci) == 0) devs.Add((d, pci.PciDeviceId));
                    foreach (Display.AdaptorInfo a in Display.DX2Adaptors())
                    {
                        if (a.VendorId != 0x10DE) continue;
                        uint id = ((uint)a.DeviceId << 16) | 0x10DE;
                        int k = devs.FindIndex(x => x.id == id);
                        if (k < 0) continue;
                        // shortcut: two identical cards pair up in enumeration order, not by bus
                        m["luid_0x" + ((uint)(a.AdapterLuid >> 32)).ToString("x8") + "_0x" + ((uint)a.AdapterLuid).ToString("x8")] = devs[k].dev;
                        devs.RemoveAt(k);
                    }
                }
                catch { }   // no NVIDIA driver: nvml.dll missing
                return m;
            }
        }

        // "pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D" -> "luid_0x..._0x..._phys_0_eng_3"
        private static string engineKey(string inst)
        {
            int a = inst.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int b = inst.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            return a >= 0 && b > a ? inst.Substring(a, b - a) : inst;
        }

        private void reportUnavailable()
        {
            if (_unavailableReported)
                return;
            _unavailableReported = true;
            post(null, null);
        }

        private void post(string text, string tip)
        {
            SynchronizationContext ctx = _uiContext;
            Action<string, string> cb = _onSample;
            if (ctx != null)
                ctx.Post(delegate { cb(text, tip); }, null);
            else
                cb(text, tip);
        }

        private static void disposeList(List<PerformanceCounter> list)
        {
            if (list == null)
                return;
            foreach (PerformanceCounter pc in list)
            {
                try { pc.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            disposeTimers();
            disposeList(Interlocked.Exchange(ref _counters, new List<PerformanceCounter>()));
        }

        private void disposeTimers()
        {
            // plain dispose; a callback that already passed its _disposed check
            // is still safe - counter access is try/caught and post() is guarded
            // by the caller's IsDisposed check on the UI side
            Timer t1 = Interlocked.Exchange(ref _sampleTimer, null);
            Timer t2 = Interlocked.Exchange(ref _enumTimer, null);
            t1?.Dispose();
            t2?.Dispose();
        }
    }
}
