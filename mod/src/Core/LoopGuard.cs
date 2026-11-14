using System;
using System.Collections.Generic;

namespace DelusionMP.Core
{
    /// <summary>
    /// Prevents feedback loops. Applying state from the host runs the same game code
    /// path that produced it (SetFact -> triggers -> spawners -> doors), so every
    /// outbound hook must be suppressed while a remote application is in progress.
    ///
    /// This is the single most important correctness mechanism in the mod: without it
    /// a single replicated fact cascades into an exponentially growing event storm.
    /// </summary>
    public static class LoopGuard
    {
        [ThreadStatic] private static int _depth;

        public static bool ApplyingRemote => _depth > 0;

        public static void Apply(Action action)
        {
            _depth++;
            try { action(); }
            finally { _depth--; }
        }

        public static T Apply<T>(Func<T> func)
        {
            _depth++;
            try { return func(); }
            finally { _depth--; }
        }
    }

    /// <summary>
    /// Remembers the last value we sent or observed per fact key, so re-setting a fact
    /// to the value it already has produces no traffic. Several triggers in this game
    /// re-set the same fact every frame (e.g. door `factToSet` on each interaction).
    /// </summary>
    public sealed class FactShadow
    {
        private readonly Dictionary<string, bool> _bools = new Dictionary<string, bool>(256);
        private readonly Dictionary<string, int> _ints = new Dictionary<string, int>(256);

        /// <summary>True when this bool value is new or changed.</summary>
        public bool ObserveBool(string key, bool value)
        {
            if (_bools.TryGetValue(key, out var old) && old == value) return false;
            _bools[key] = value;
            return true;
        }

        public bool ObserveInt(string key, int value)
        {
            if (_ints.TryGetValue(key, out var old) && old == value) return false;
            _ints[key] = value;
            return true;
        }

        public bool TryGetBool(string key, out bool value) => _bools.TryGetValue(key, out value);
        public bool TryGetInt(string key, out int value) => _ints.TryGetValue(key, out value);

        public void Reset()
        {
            _bools.Clear();
            _ints.Clear();
        }

        /// <summary>Seed from a full snapshot so we do not re-send everything on connect.</summary>
        public void Seed(IReadOnlyDictionary<string, bool> bools, IReadOnlyDictionary<string, int> ints)
        {
            if (bools != null) foreach (var kv in bools) _bools[kv.Key] = kv.Value;
            if (ints != null) foreach (var kv in ints) _ints[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// At-most-once delivery with replay for late joiners.
    ///
    /// Every world event carries a monotonic id per object key. The host keeps the
    /// journal so a player who joins mid-session receives the effects of everything it
    /// missed without re-running reward code.
    /// </summary>
    public sealed class EventJournal
    {
        public struct Entry
        {
            public long Id;
            public string ObjectKey;
            public PacketKind Kind;
            public byte[] Payload;
        }

        private readonly List<Entry> _entries = new List<Entry>(4096);
        private long _nextId = 1;
        private readonly Dictionary<string, long> _lastApplied = new Dictionary<string, long>();

        public int Count => _entries.Count;

        public Entry Append(string objectKey, PacketKind kind, byte[] payload)
        {
            var e = new Entry { Id = _nextId++, ObjectKey = objectKey, Kind = kind, Payload = payload };
            _entries.Add(e);
            return e;
        }

        /// <summary>True when this id has not been applied yet for the object.</summary>
        public bool ShouldApply(string objectKey, long id)
        {
            if (_lastApplied.TryGetValue(objectKey, out var last) && id <= last) return false;
            _lastApplied[objectKey] = id;
            return true;
        }

        /// <summary>Entries a newly connected client has not seen.</summary>
        public IEnumerable<Entry> Since(long id)
        {
            foreach (var e in _entries)
                if (e.Id > id) yield return e;
        }

        public long LastId => _nextId - 1;
    }
}
