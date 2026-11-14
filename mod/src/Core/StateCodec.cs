using System;
using System.Collections.Generic;
using System.Text;
using DelusionMP.Game;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace DelusionMP.Core
{
    /// <summary>
    /// Wire format for one SaveableObject's captured state.
    ///
    /// SaveableObject.CaptureState() returns an IL2CPP Dictionary&lt;string, object&gt;: one
    /// entry per ISaveable component on the object (keyed by component type), each value
    /// being that component's own save data - a game class or struct, sometimes holding
    /// lists, arrays, enums and nested objects. An untouched object (nothing changed from
    /// its scene default) captures null; the save file only stores what changed.
    ///
    /// The codec walks that graph with IL2CPP reflection and writes a small tagged tree:
    /// dictionaries by entry, lists/arrays by element, objects by field (with their type
    /// name). <see cref="Build"/> reverses it on the peer: it instantiates the same IL2CPP
    /// types, fills them, and the caller hands the result to the game's own RestoreState.
    /// Building (rather than patching the peer's own capture) matters: the peer's object is
    /// usually still at its default and captures null, which is exactly the case a sync has
    /// to handle - the host looted a chest the client has never touched.
    /// </summary>
    public static class StateCodec
    {
        private const byte TNull = 0;
        private const byte TBool = 1;
        private const byte TI32 = 2;
        private const byte TF32 = 3;
        private const byte TStr = 4;
        private const byte TI64 = 5;
        private const byte TEnum = 6;
        private const byte TVec2 = 7;
        private const byte TVec3 = 8;
        private const byte TQuat = 9;
        private const byte TColor = 10;
        private const byte TObj = 11;
        private const byte TSkip = 12;
        private const byte TDict = 13;
        private const byte TList = 14;
        private const byte TArray = 15;
        private const byte TF64 = 16;
        private const byte TU8 = 17;
        private const byte TI16 = 18;

        private const int MaxDepth = 8;
        private const int MaxFields = 96;
        private const int MaxElems = 2048;

        private static readonly Il2CppSystem.Reflection.BindingFlags Flags =
            Il2CppSystem.Reflection.BindingFlags.Instance |
            Il2CppSystem.Reflection.BindingFlags.Public |
            Il2CppSystem.Reflection.BindingFlags.NonPublic;

        // ------------------------------------------------------------------ describe

        public static string TypeNameOf(object o)
        {
            if (o == null) return "null";
            try
            {
                var boxed = o as Il2CppSystem.Object;
                if (boxed != null) return boxed.GetIl2CppType()?.FullName ?? "il2cpp?";
                return o.GetType().FullName;
            }
            catch { return "?"; }
        }

        public static string DescribeFields(object o)
        {
            try { return Describe(Encode(o), 400); }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>Human-readable dump of an encoded state (rig diagnostics).</summary>
        public static string Describe(byte[] body, int max = 4000)
        {
            var sb = new StringBuilder();
            try { DescribeValue(new Reader(body), sb, max); }
            catch (Exception e) { sb.Append(" <").Append(e.Message).Append('>'); }
            return sb.Length > max ? sb.ToString(0, max) + "..." : sb.ToString();
        }

        private static void DescribeValue(Reader r, StringBuilder sb, int max)
        {
            if (sb.Length > max) throw new InvalidOperationException("truncated");
            byte tag = r.U8();
            switch (tag)
            {
                case TNull: sb.Append("null"); return;
                case TSkip: sb.Append('?'); return;
                case TBool: sb.Append(r.Bool()); return;
                case TI32: sb.Append(r.I32()); return;
                case TU8: sb.Append(r.U8()); return;
                case TI16: sb.Append((short)r.U16()); return;
                case TF32: sb.Append(r.F32().ToString("G4")); return;
                case TF64: sb.Append(BitConverter.Int64BitsToDouble(r.I64()).ToString("G6")); return;
                case TI64: sb.Append(r.I64()); return;
                case TStr: sb.Append('"').Append(r.Str()).Append('"'); return;
                case TEnum: { var t = r.Str(); sb.Append(Short(t)).Append('=').Append(r.I32()); return; }
                case TVec2: sb.Append($"({r.F32():G4},{r.F32():G4})"); return;
                case TVec3: sb.Append($"({r.F32():G4},{r.F32():G4},{r.F32():G4})"); return;
                case TQuat:
                case TColor: sb.Append($"({r.F32():G3},{r.F32():G3},{r.F32():G3},{r.F32():G3})"); return;
                case TObj:
                {
                    var t = r.Str(); int n = r.U16();
                    sb.Append(Short(t)).Append('{');
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(r.Str()).Append('=');
                        DescribeValue(r, sb, max);
                    }
                    sb.Append('}');
                    return;
                }
                case TDict:
                {
                    r.Str(); int n = r.U16();
                    sb.Append('{');
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) sb.Append("; ");
                        DescribeValue(r, sb, max); sb.Append(": "); DescribeValue(r, sb, max);
                    }
                    sb.Append('}');
                    return;
                }
                case TList:
                case TArray:
                {
                    r.Str(); int n = r.U16();
                    sb.Append('[');
                    for (int i = 0; i < n; i++) { if (i > 0) sb.Append(", "); DescribeValue(r, sb, max); }
                    sb.Append(']');
                    return;
                }
                default: throw new ProtocolException("codec: unknown tag " + tag);
            }
        }

        private static string Short(string t)
        {
            int i = t.LastIndexOf('.');
            return i >= 0 ? t.Substring(i + 1) : t;
        }

        // ------------------------------------------------------------------ encode

        public static byte[] Encode(object state)
        {
            var w = new Writer();
            EncodeValue(w, state as Il2CppSystem.Object, state, 0);
            return w.ToArray();
        }

        private static void EncodeValue(Writer w, Il2CppSystem.Object boxed, object managed, int depth)
        {
            if (managed == null && boxed == null) { w.U8(TNull); return; }

            if (boxed == null)
            {
                switch (managed)
                {
                    case bool b: w.U8(TBool).Bool(b); return;
                    case int i: w.U8(TI32).I32(i); return;
                    case float f: w.U8(TF32).F32(f); return;
                    case long l: w.U8(TI64).I64(l); return;
                    case string s: w.U8(TStr).Str(s); return;
                }
                w.U8(TSkip);
                return;
            }

            Il2CppSystem.Type type;
            string full;
            try { type = boxed.GetIl2CppType(); full = type?.FullName; }
            catch { type = null; full = null; }
            if (type == null || string.IsNullOrEmpty(full)) { w.U8(TSkip); return; }

            switch (full)
            {
                case "System.Boolean": w.U8(TBool).Bool(boxed.Unbox<bool>()); return;
                case "System.Int32": w.U8(TI32).I32(boxed.Unbox<int>()); return;
                case "System.Single": w.U8(TF32).F32(boxed.Unbox<float>()); return;
                case "System.Double": w.U8(TF64).I64(BitConverter.DoubleToInt64Bits(boxed.Unbox<double>())); return;
                case "System.Int64": w.U8(TI64).I64(boxed.Unbox<long>()); return;
                case "System.Byte": w.U8(TU8).U8(boxed.Unbox<byte>()); return;
                case "System.Int16": w.U8(TI16).U16((ushort)boxed.Unbox<short>()); return;
                case "System.String": w.U8(TStr).Str(boxed.ToString()); return;
                case "UnityEngine.Vector2": { var v = boxed.Unbox<Vector2>(); w.U8(TVec2).F32(v.x).F32(v.y); return; }
                case "UnityEngine.Vector3": { var v = boxed.Unbox<Vector3>(); w.U8(TVec3).F32(v.x).F32(v.y).F32(v.z); return; }
                case "UnityEngine.Quaternion": { var q = boxed.Unbox<Quaternion>(); w.U8(TQuat).F32(q.x).F32(q.y).F32(q.z).F32(q.w); return; }
                case "UnityEngine.Color": { var c = boxed.Unbox<Color>(); w.U8(TColor).F32(c.r).F32(c.g).F32(c.b).F32(c.a); return; }
            }

            if (type.IsEnum)
            {
                w.U8(TEnum).Str(full).I32(EnumValue(boxed, type));
                return;
            }
            if (depth >= MaxDepth) { w.U8(TSkip); return; }

            // Unity objects (GameObject, Transform, ScriptableObject refs) are scene identity,
            // not data; they cannot cross processes.
            if (IsUnityObject(type)) { w.U8(TSkip); return; }

            if (type.IsArray)
            {
                if (Trace) Crumb("array " + full);
                var arr = boxed.TryCast<Il2CppSystem.Array>();
                if (arr == null) { w.U8(TSkip); return; }
                int n = Math.Min(arr.Length, MaxElems);
                w.U8(TArray).Str(type.GetElementType()?.FullName ?? "").U16((ushort)n);
                for (int i = 0; i < n; i++) EncodeValue(w, arr.GetValue(i), null, depth + 1);
                return;
            }

            if (full.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal))
            {
                // Concrete generic cast + foreach (the same access FactSync uses); interop
                // interface dispatch on these objects faults natively.
                var dict = boxed.TryCast<Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object>>();
                if (dict == null) { Crumb("skip dict " + full); w.U8(TSkip); return; }
                var keys = new List<string>();
                foreach (var kv in dict) { keys.Add(kv.Key); if (keys.Count >= MaxElems) break; }
                keys.Sort(StringComparer.Ordinal);
                w.U8(TDict).Str(full).U16((ushort)keys.Count);
                foreach (var k in keys)
                {
                    w.U8(TStr).Str(k);
                    Il2CppSystem.Object v = null;
                    dict.TryGetValue(k, out v);
                    EncodeValue(w, v, null, depth + 1);
                }
                return;
            }

            if (full.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal))
            {
                // Reflection Count/get_Item: works for any element type without interface casts.
                int n;
                Il2CppSystem.Reflection.MethodInfo getItem;
                try
                {
                    n = type.GetProperty("Count").GetValue(boxed).Unbox<int>();
                    getItem = type.GetMethod("get_Item");
                }
                catch { w.U8(TSkip); return; }
                n = Math.Min(n, MaxElems);
                w.U8(TList).Str(full).U16((ushort)n);
                for (int i = 0; i < n; i++)
                {
                    Il2CppSystem.Object v = null;
                    try
                    {
                        var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                        args[0] = BoxStruct(i);
                        v = getItem.Invoke(boxed, args);
                    }
                    catch { }
                    EncodeValue(w, v, null, depth + 1);
                }
                return;
            }

            // Only game data is walked field by field. Framework internals (IntPtr, delegates,
            // RuntimeType, reflection, threading) hold raw pointers, and reading those
            // through IL2CPP reflection faults natively - uncatchable.
            if (Trace) Crumb("obj " + full);
            if (full.StartsWith("System.", StringComparison.Ordinal) ||
                full.StartsWith("Il2Cpp", StringComparison.Ordinal) ||
                full.StartsWith("Mono.", StringComparison.Ordinal) ||
                IsDelegate(type))
            {
                w.U8(TSkip);
                return;
            }

            Il2CppSystem.Reflection.FieldInfo[] fields = null;
            try { fields = type.GetFields(Flags); }
            catch (Exception ex) { Log.Debug("codec fields " + full + ": " + ex.Message); }
            if (fields == null) { w.U8(TSkip); return; }

            var rows = new List<Il2CppSystem.Reflection.FieldInfo>();
            foreach (var f in fields)
            {
                if (f == null || f.IsStatic || f.IsLiteral) continue;
                try
                {
                    var ft = f.FieldType;
                    if (ft == null || ft.IsPointer || ft.IsByRef) continue;
                    var fn = ft.FullName ?? "";
                    if (fn == "System.IntPtr" || fn == "System.UIntPtr") continue;
                }
                catch { continue; }
                if (rows.Count >= MaxFields) break;
                rows.Add(f);
            }
            w.U8(TObj).Str(full).U16((ushort)rows.Count);
            foreach (var f in rows)
            {
                w.Str(f.Name);
                Il2CppSystem.Object value = null;
                try { value = f.GetValue(boxed); }
                catch { w.U8(TSkip); continue; }
                EncodeValue(w, value, null, depth + 1);
            }
        }

        /// <summary>Write the type being walked to ddmp_crumbs.txt before touching it.</summary>
        public static bool Trace;
        private static System.IO.StreamWriter _crumbs;
        public static void Crumb(string line)
        {
            try
            {
                _crumbs ??= new System.IO.StreamWriter(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_crumbs.txt"), false) { AutoFlush = true };
                _crumbs.WriteLine(line);
            }
            catch { }
        }

        private static bool IsDelegate(Il2CppSystem.Type t)
        {
            try { return Il2CppType.Of<Il2CppSystem.Delegate>().IsAssignableFrom(t); }
            catch { return true; }
        }

        private static bool IsUnityObject(Il2CppSystem.Type t)
        {
            try
            {
                var uo = Il2CppType.Of<UnityEngine.Object>();
                return uo != null && uo.IsAssignableFrom(t);
            }
            catch { return false; }
        }

        /// <summary>Raw read of a boxed enum: interop's Unbox&lt;int&gt; refuses enum classes.</summary>
        private static unsafe int EnumValue(Il2CppSystem.Object boxed, Il2CppSystem.Type type)
        {
            string under = "System.Int32";
            try { under = Il2CppSystem.Enum.GetUnderlyingType(type)?.FullName ?? under; } catch { }
            IntPtr p = IL2CPP.il2cpp_object_unbox(boxed.Pointer);
            switch (under)
            {
                case "System.Byte": return *(byte*)p;
                case "System.SByte": return *(sbyte*)p;
                case "System.Int16": return *(short*)p;
                case "System.UInt16": return *(ushort*)p;
                case "System.Int64": case "System.UInt64": return (int)*(long*)p;
                default: return *(int*)p;
            }
        }

        // ------------------------------------------------------------------ build

        /// <summary>
        /// Reconstruct the IL2CPP object graph an encoded state describes. Returns null for
        /// an encoded null (and when the shape cannot be rebuilt).
        /// </summary>
        public static bool BuildTrace;
        public static Il2CppSystem.Object Build(byte[] body)
        {
            BuildTrace = _builds++ < 300;
            if (BuildTrace) Crumb("BUILD " + Describe(body, 300));
            var r = new Reader(body);
            var o = BuildValue(r, null);
            if (BuildTrace) Crumb("BUILT ok");
            return o;
        }

        /// <summary>Legacy entry point (selftest): rebuild and report whether it worked.</summary>
        public static bool ApplyFields(object target, byte[] payload) => Build(payload) != null;

        private static readonly Dictionary<string, Il2CppSystem.Type> _types =
            new Dictionary<string, Il2CppSystem.Type>(StringComparer.Ordinal);

        public static Il2CppSystem.Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_types.TryGetValue(fullName, out var t)) return t;
            t = null;
            try { t = Il2CppSystem.Type.GetType(fullName); } catch { }
            if (t == null)
            {
                try
                {
                    foreach (var asm in Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { t = asm.GetType(fullName); } catch { }
                        if (t != null) break;
                    }
                }
                catch { }
            }
            if (t == null) Log.Warn("codec: cannot resolve type " + fullName);
            _types[fullName] = t;
            return t;
        }

        private static Il2CppSystem.Object New(Il2CppSystem.Type t)
        {
            if (BuildTrace) Crumb(" new " + t.FullName);
            try { var o = Il2CppSystem.Activator.CreateInstance(t); if (o != null) return o; }
            catch { }
            // No default constructor: allocate without running one (fields stay zeroed and
            // are then filled from the wire).
            try
            {
                IntPtr klass = IL2CPP.il2cpp_class_from_system_type(t.Pointer);
                IntPtr obj = IL2CPP.il2cpp_object_new(klass);
                return obj == IntPtr.Zero ? null : new Il2CppSystem.Object(obj);
            }
            catch (Exception e) { Log.Warn($"codec: cannot create {t.FullName}: {e.Message}"); return null; }
        }

        private static unsafe Il2CppSystem.Object BoxStruct<T>(T value) where T : unmanaged
        {
            IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
            IntPtr p = IL2CPP.il2cpp_value_box(klass, (IntPtr)(&value));
            return new Il2CppSystem.Object(p);
        }

        private static int _builds;
        private static Il2CppSystem.Object BuildValue(Reader r, Il2CppSystem.Type expected)
        {
            byte tag = r.U8();
            if (BuildTrace) Crumb("b tag=" + tag);
            switch (tag)
            {
                case TNull: case TSkip: return null;
                case TBool: return BoxStruct(r.Bool());
                case TI32: return BoxStruct(r.I32());
                case TU8: return BoxStruct(r.U8());
                case TI16: return BoxStruct((short)r.U16());
                case TF32: return BoxStruct(r.F32());
                case TF64: return BoxStruct(BitConverter.Int64BitsToDouble(r.I64()));
                case TI64: return BoxStruct(r.I64());
                case TStr: return (Il2CppSystem.String)r.Str();
                case TVec2: return BoxStruct(new Vector2(r.F32(), r.F32()));
                case TVec3: return BoxStruct(new Vector3(r.F32(), r.F32(), r.F32()));
                case TQuat: return BoxStruct(new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()));
                case TColor: return BoxStruct(new Color(r.F32(), r.F32(), r.F32(), r.F32()));
                case TEnum:
                {
                    var t = ResolveType(r.Str());
                    int v = r.I32();
                    if (t == null) return null;
                    try { return Il2CppSystem.Enum.ToObject(t, BoxStruct(v)); }
                    catch { return null; }
                }
                case TObj:
                {
                    var t = ResolveType(r.Str());
                    int n = r.U16();
                    var obj = t != null ? New(t) : null;
                    for (int i = 0; i < n; i++)
                    {
                        string name = r.Str();
                        Il2CppSystem.Reflection.FieldInfo f = null;
                        try { f = t?.GetField(name, Flags); } catch { }
                        if (BuildTrace) Crumb(" field " + name);
                        var v = BuildValue(r, f?.FieldType);
                        if (obj == null || f == null) continue;
                        // A null for a value-type field means "could not encode" - keep the
                        // field's zero value rather than failing the whole object.
                        if (v == null && f.FieldType.IsValueType) continue;
                        if (BuildTrace) Crumb(" set " + name);
                        try { f.SetValue(obj, v); }
                        catch (Exception e) { Log.Debug($"codec set {t.FullName}.{name}: {e.Message}"); }
                    }
                    return obj;
                }
                case TDict:
                {
                    var t = ResolveType(r.Str());
                    int n = r.U16();
                    var obj = t != null ? New(t) : null;
                    var setItem = t?.GetMethod("set_Item");
                    for (int i = 0; i < n; i++)
                    {
                        var k = BuildValue(r, null);
                        var v = BuildValue(r, null);
                        if (obj == null || setItem == null || k == null) continue;
                        try
                        {
                            var args = new Il2CppReferenceArray<Il2CppSystem.Object>(2);
                            args[0] = k; args[1] = v;
                            setItem.Invoke(obj, args);
                        }
                        catch (Exception e) { Log.Debug("codec dict add: " + e.Message); }
                    }
                    return obj;
                }
                case TList:
                {
                    var t = ResolveType(r.Str());
                    int n = r.U16();
                    var obj = t != null ? New(t) : null;
                    var add = t?.GetMethod("Add");
                    for (int i = 0; i < n; i++)
                    {
                        var v = BuildValue(r, null);
                        if (obj == null || add == null) continue;
                        try
                        {
                            var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                            args[0] = v;
                            add.Invoke(obj, args);
                        }
                        catch (Exception e) { Log.Debug("codec list add: " + e.Message); }
                    }
                    return obj;
                }
                case TArray:
                {
                    var et = ResolveType(r.Str());
                    int n = r.U16();
                    Il2CppSystem.Array arr = null;
                    try { if (et != null) arr = Il2CppSystem.Array.CreateInstance(et, n); } catch { }
                    for (int i = 0; i < n; i++)
                    {
                        var v = BuildValue(r, et);
                        if (arr == null) continue;
                        try { arr.SetValue(v, i); } catch { }
                    }
                    return arr;
                }
                default:
                    throw new ProtocolException("codec: unknown tag " + tag);
            }
        }
    }
}
