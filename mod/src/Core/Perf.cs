using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using DelusionMP.Game;

namespace DelusionMP.Core
{
    /// <summary>
    /// Per-subsystem frame cost, and failure isolation: each subsystem runs in its own
    /// try/catch so one fault cannot silently skip every system after it for that frame.
    /// `perf` (console) prints average and worst milliseconds per frame since the last reset.
    /// </summary>
    public static class Perf
    {
        private sealed class Slot
        {
            public double TotalMs, MaxMs;
            public long Calls;
            public int Errors;
        }

        private static readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private static readonly List<string> _order = new List<string>();
        private static readonly Stopwatch _sw = new Stopwatch();
        private static long _frames;
        private static double _frameTotal, _frameMax, _thisFrame;

        public static void Run(string name, Action a)
        {
            if (!_slots.TryGetValue(name, out var s)) { s = new Slot(); _slots[name] = s; _order.Add(name); }
            long t0 = Stopwatch.GetTimestamp();
            try { a(); }
            catch (Exception e)
            {
                if (s.Errors++ < 5) Log.Error($"{name} failed: {e}");
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            s.TotalMs += ms; s.Calls++;
            if (ms > s.MaxMs) s.MaxMs = ms;
            _thisFrame += ms;
        }

        public static void EndFrame()
        {
            _frames++;
            _frameTotal += _thisFrame;
            if (_thisFrame > _frameMax) _frameMax = _thisFrame;
            _thisFrame = 0;
        }

        public static string Report(bool reset)
        {
            var sb = new StringBuilder();
            long f = Math.Max(1, _frames);
            sb.Append($"PERF frames={_frames} mod avg={_frameTotal / f:F3} ms/frame worst={_frameMax:F2} ms\n");
            foreach (var n in _order)
            {
                var s = _slots[n];
                sb.Append($"  {n,-18} avg={s.TotalMs / f:F3} ms/frame worst={s.MaxMs:F2} ms calls={s.Calls}{(s.Errors > 0 ? " errors=" + s.Errors : "")}\n");
            }
            if (reset)
            {
                foreach (var s in _slots.Values) { s.TotalMs = 0; s.MaxMs = 0; s.Calls = 0; }
                _frames = 0; _frameTotal = 0; _frameMax = 0;
            }
            return sb.ToString().TrimEnd();
        }
    }
}
