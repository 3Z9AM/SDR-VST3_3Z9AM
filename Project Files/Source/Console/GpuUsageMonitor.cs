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
// Reported value = the busiest engine (as Task Manager), clamped to 100.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Action<float> _onSample; // float % on UI thread, -1 => unavailable

        private List<PerformanceCounter> _counters = new List<PerformanceCounter>();
        private Timer _sampleTimer;
        private Timer _enumTimer;
        private bool _started;
        private bool _unavailableReported;
        private int _disposed;

        public GpuUsageMonitor(Action<float> onSample)
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
            foreach (float v in perEngine.Values) max = Math.Max(max, v);
            post(Math.Min(max, 100f));
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
            post(-1f);
        }

        private void post(float value)
        {
            SynchronizationContext ctx = _uiContext;
            Action<float> cb = _onSample;
            if (ctx != null)
                ctx.Post(delegate { cb(value); }, null);
            else
                cb(value);
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
