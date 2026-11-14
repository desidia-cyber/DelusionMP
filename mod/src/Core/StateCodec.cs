using System;
using System.Collections.Generic;
using DelusionMP.Game;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace DelusionMP.Core
{
    /// <summary>
    /// Wire format for one saveable component's state.
    ///
    /// `ISaveable.CaptureState()` returns an `object`. In this game most implementations box
    /// a game struct or class into it, and the concrete type varies per component (SaveData,
    /// FactSaveData, task data, plain dictionaries, ...). Hand-writing a payload per
    /// component is neither maintainable nor complete, so this walks the boxed object with
    /// IL2CPP's own reflection (`Il2CppSystem.Type.GetFields`, `Object.FieldGetter`) and
    /// emits a tiny tagged tree of its fields.
    ///
    /// The client does not need to construct a game object at all: it captures its *own*
    /// state for the same saveable (same shape) and patches the fields it received with
    /// `FieldSetter`, then hands that object to the game's RestoreState. That avoids
    /// instantiating IL2CPP types through managed code entirely.
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
        private const byte TFields = 11;
        private const byte TSkip = 12;

        private const int MaxDepth = 3;
        private const int MaxFields = 64;

        private static readonly Il2CppSystem.Reflection.BindingFlags Flags =
            Il2CppSystem.Reflection.BindingFlags.Instance |
            Il2CppSystem.Reflection.BindingFlags.Public |
            Il2CppSystem.Reflection.BindingFlags.NonPublic;

        // ------------------------------------------------------------------ describe

        public static string DescribeFields(object o)
        {
            try
            {
                var boxed = o as Il2CppSystem.Object;
                if (boxed == null) return "not il2cpp";
                var t = boxed.GetIl2CppType();
                if (t == null) return "no type";
                var fields = t.GetFields(Flags);
                if (fields == null) return "no fields";
                var sb = new System.Text.StringBuilder(200);
                foreach (var f in fields)
                {
                    if (sb.Length > 220) break;
                    sb.Append(f.FieldType?.Name).Append(' ').Append(f.Name).Append("; ");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

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

            // Plain managed primitives (rare, but the codec is also used on managed values).
            if (managed != null)
            {
                switch (managed)
                {
                    case bool b: w.U8(TBool).Bool(b); return;
                    case int i: w.U8(TI32).I32(i); return;
                    case float f: w.U8(TF32).F32(f); return;
                    case long l: w.U8(TI64).I64(l); return;
                    case string s: w.U8(TStr).Str(s); return;
                }
            }

            if (boxed == null) { w.U8(TSkip); return; }

            string full;
            try { full = boxed.GetIl2CppType()?.FullName; }
            catch { full = null; }
            if (string.IsNullOrEmpty(full)) { w.U8(TSkip); return; }

            switch (full)
            {
                case "System.Boolean": w.U8(TBool).Bool(boxed.Unbox<bool>()); return;
                case "System.Int32": w.U8(TI32).I32(boxed.Unbox<int>()); return;
                case "System.Single": w.U8(TF32).F32(boxed.Unbox<float>()); return;
                case "System.Int64": w.U8(TI64).I64(boxed.Unbox<long>()); return;
                case "System.String": w.U8(TStr).Str(boxed.ToString()); return;
                case "UnityEngine.Vector2":
                {
                    var v = boxed.Unbox<Vector2>(); w.U8(TVec2).F32(v.x).F32(v.y); return;
                }
                case "UnityEngine.Vector3":
                {
                    var v = boxed.Unbox<Vector3>(); w.U8(TVec3).F32(v.x).F32(v.y).F32(v.z); return;
                }
                case "UnityEngine.Quaternion":
                {
                    var q = boxed.Unbox<Quaternion>();
                    w.U8(TQuat).F32(q.x).F32(q.y).F32(q.z).F32(q.w);
                    return;
                }
                case "UnityEngine.Color":
                {
                    var c = boxed.Unbox<Color>();
                    w.U8(TColor).F32(c.r).F32(c.g).F32(c.b).F32(c.a);
                    return;
                }
            }

            if (depth >= MaxDepth) { w.U8(TSkip); return; }

            Il2CppSystem.Reflection.FieldInfo[] fields = null;
            try { fields = boxed.GetIl2CppType().GetFields(Flags); }
            catch (Exception e) { Log.Debug("codec fields " + full + ": " + e.Message); }
            if (fields == null || fields.Length == 0)
            {
                // Enums come through as their own type with no instance fields; the
                // underlying value is an int in every enum this game declares.
                try { w.U8(TEnum).Str(full).I32(boxed.Unbox<int>()); return; }
                catch { w.U8(TSkip); return; }
            }

            int count = Math.Min(fields.Length, MaxFields);
            w.U8(TFields).Str(full).U16((ushort)count);
            int written = 0;
            foreach (var f in fields)
            {
                if (written >= count) break;
                Il2CppSystem.Object value = null;
                try { boxed.FieldGetter(f.Name, full, ref value); }
                catch { continue; }
                w.Str(f.Name);
                EncodeValue(w, value, null, depth + 1);
                written++;
            }
        }

        // ------------------------------------------------------------------ apply
        //
        // Patch the peer's own captured state object with the host's field values.

        public static bool ApplyFields(object target, byte[] payload)
        {
            var boxed = target as Il2CppSystem.Object;
            if (boxed == null) return false;
            var r = new Reader(payload);
            return ApplyValue(boxed, r, 0);
        }

        private static bool ApplyValue(Il2CppSystem.Object target, Reader r, int depth)
        {
            var tag = r.U8();
            switch (tag)
            {
                case TNull: return true;
                case TBool: case TI32: case TF32: case TI64: case TStr:
                case TVec2: case TVec3: case TQuat: case TColor:
                case TEnum:
                    SkipScalar(tag, r);
                    return true;
                case TSkip:
                    return true;
                case TFields:
                {
                    var typeName = r.Str();
                    int count = r.U16();
                    for (int i = 0; i < count; i++)
                    {
                        var field = r.Str();
                        ApplyFieldValue(target, typeName, field, r, depth + 1);
                    }
                    return true;
                }
                default:
                    throw new ProtocolException("codec: unknown tag " + tag);
            }
        }

        private static void ApplyFieldValue(Il2CppSystem.Object target, string typeName,
                                            string field, Reader r, int depth)
        {
            var tag = r.U8();
            switch (tag)
            {
                case TNull: case TSkip: return;
                case TBool:
                {
                    bool v = r.Bool();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TI32:
                {
                    int v = r.I32();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TF32:
                {
                    float v = r.F32();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TI64:
                {
                    long v = r.I64();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TStr:
                {
                    string v = r.Str();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TEnum:
                {
                    r.Str(); int v = r.I32();
                    try { target.FieldSetter(field, typeName, v); } catch { }
                    return;
                }
                case TVec2:
                {
                    r.F32(); r.F32();   // not settable through FieldSetter (needs il2cpp boxing)
                    return;
                }
                case TVec3:
                {
                    r.F32(); r.F32(); r.F32();
                    return;
                }
                case TQuat:
                {
                    r.F32(); r.F32(); r.F32(); r.F32();
                    return;
                }
                case TColor:
                {
                    r.F32(); r.F32(); r.F32(); r.F32();
                    return;
                }
                case TFields:
                {
                    // Nested object: get the peer's own child object and recurse into it.
                    r.Str();                 // nested type name (unused: FieldGetter knows it)
                    int count = r.U16();
                    Il2CppSystem.Object child = null;
                    try { target.FieldGetter(field, typeName, ref child); } catch { }
                    for (int i = 0; i < count; i++)
                    {
                        var childField = r.Str();
                        if (child == null)
                        {
                            // Cannot read the child type name without the header; re-read
                            // structure conservatively.
                            SkipFieldValue(r, typeName, childField, 0);
                            continue;
                        }
                        var childType = child.GetIl2CppType()?.FullName ?? typeName;
                        ApplyFieldValue(child, childType, childField, r, depth + 1);
                    }
                    return;
                }
                default:
                    throw new ProtocolException("codec: unknown field tag " + tag);
            }
        }

        private static void SkipFieldValue(Reader r, string typeName, string field, int depth)
        {
            var tag = r.U8();
            switch (tag)
            {
                case TNull: case TSkip: return;
                case TBool: r.Bool(); return;
                case TI32: case TF32: r.I32(); return;
                case TI64: r.I64(); return;
                case TStr: r.Str(); return;
                case TEnum: r.Str(); r.I32(); return;
                case TVec2: r.I32(); r.I32(); return;
                case TVec3: r.I32(); r.I32(); r.I32(); return;
                case TQuat: r.I32(); r.I32(); r.I32(); r.I32(); return;
                case TColor: r.I32(); r.I32(); r.I32(); r.I32(); return;
                case TFields:
                {
                    r.Str(); int n = r.U16();
                    for (int i = 0; i < n; i++)
                    {
                        var f = r.Str();
                        SkipFieldValue(r, typeName, f, depth + 1);
                    }
                    return;
                }
            }
        }

        private static void SkipScalar(byte tag, Reader r)
        {
            switch (tag)
            {
                case TBool: r.Bool(); return;
                case TI32: case TF32: r.I32(); return;
                case TI64: r.I64(); return;
                case TStr: r.Str(); return;
                case TEnum: r.Str(); r.I32(); return;
                case TVec2: r.I32(); r.I32(); return;
                case TVec3: r.I32(); r.I32(); r.I32(); return;
                case TQuat: r.I32(); r.I32(); r.I32(); r.I32(); return;
                case TColor: r.I32(); r.I32(); r.I32(); r.I32(); return;
            }
        }
    }
}
