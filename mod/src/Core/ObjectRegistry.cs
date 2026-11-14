using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DelusionMP.Core
{
    /// <summary>
    /// Stable identity for world objects across two processes.
    ///
    /// The game already provides the key: SaveableObject.uniqueIdentifier is a
    /// [SerializeField] string on 4 804 components (4 754 non-empty, 4 493 distinct in
    /// the shipping build). GetInstanceID()/path_id differ per process and must never
    /// cross the wire.
    ///
    /// Key format: "&lt;scene&gt;|&lt;uniqueIdentifier&gt;", or for the handful without an
    /// identifier "&lt;scene&gt;|#path/&lt;hierarchyPath&gt;" built from transforms.
    /// </summary>
    public sealed class ObjectRegistry
    {
        private static ObjectRegistry _instance;
        public static ObjectRegistry Instance => _instance ??= new ObjectRegistry();

        private readonly Dictionary<string, GameObject> _byKey =
            new Dictionary<string, GameObject>(8192, StringComparer.Ordinal);

        private string _scene = "";
        private bool _dirty = true;

        public string CurrentScene => _scene;

        /// <summary>True when the scene changed and the table must be rebuilt.</summary>
        public bool NeedsRebuild => _dirty;

        public void Invalidate() => _dirty = true;

        public void Rebuild(string sceneName)
        {
            _scene = sceneName ?? "";
            _byKey.Clear();
            BuildTable();
            _dirty = false;
        }

        private void BuildTable()
        {
            // SaveableObject.GatherComponentIdentifiers() already builds an internal
            // lookup, but it is private. Walking the loaded SaveableObjects is cheap
            // and does not depend on save internals.
            var saveables = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
            if (saveables == null) return;

            var used = new Dictionary<string, int>(saveables.Length, StringComparer.Ordinal);
            foreach (var so in saveables)
            {
                if (so == null) continue;
                string id = null;
                try { id = so.GetUniqueIdentifier(); } catch { /* not initialised yet */ }

                string key;
                if (!string.IsNullOrEmpty(id))
                {
                    key = _scene + "|" + id;
                }
                else
                {
                    key = _scene + "|#path/" + HierarchyPath(so.transform);
                }

                // Duplicate identifiers exist in the shipped data (4 754 non-empty but only
                // 4 493 distinct). The first occurrence always keeps the plain key, so a
                // peer that addresses an object by "scene|identifier" resolves to the same
                // object on both sides; the extra ones get an ordered suffix.
                if (used.TryGetValue(key, out var n))
                {
                    used[key] = n + 1;
                    key = key + "#" + (n + 1);
                }
                else
                {
                    used[key] = 0;
                }

                _byKey[key] = so.gameObject;
            }
        }

        public static string HierarchyPath(Transform t)
        {
            if (t == null) return "";
            var sb = new StringBuilder(96);
            var stack = new List<string>(8);
            while (t != null)
            {
                stack.Add(t.name + ":" + t.GetSiblingIndex());
                t = t.parent;
            }
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(stack[i]);
            }
            return sb.ToString();
        }

        public bool TryResolve(string key, out GameObject obj) => _byKey.TryGetValue(key, out obj);

        public IEnumerable<string> SampleKeys(int n)
        {
            int i = 0;
            foreach (var k in _byKey.Keys)
            {
                if (i++ >= n) break;
                yield return k;
            }
        }

        public int Count => _byKey.Count;

        /// <summary>
        /// Fingerprint of the loaded object table, exchanged at handshake. A mismatch
        /// means two different game builds or a mod conflict; the session must refuse
        /// to start rather than desync silently.
        /// </summary>
        public string TableHash()
        {
            var keys = new List<string>(_byKey.Keys);
            keys.Sort(StringComparer.Ordinal);
            ulong h = 1469598103934665603UL; // FNV-1a 64
            foreach (var k in keys)
            {
                foreach (char c in k)
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
            }
            return h.ToString("X16");
        }
    }
}
