using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DelusionMP.Core
{
    /// <summary>
    /// Wire framing for DelusionMP. Mirrors the layered design proven in DollsMP
    /// (crates/dollsmp-core/src/protocol.rs): a fixed 18-byte little-endian header
    /// followed by a payload, with strict version negotiation so a mismatch fails
    /// the handshake instead of producing a silent desync.
    /// </summary>
    public static class Proto
    {
        /// Magic bytes at the start of every datagram.
        public static readonly byte[] Magic = { (byte)'D', (byte)'D', (byte)'M', (byte)'P' };

        /// Bump on any wire-incompatible change to the header or body layout.
        public const ushort Version = 1;

        /// Fixed header size in bytes: magic(4) ver(2) kind(1) flags(1) seq(2) ack(2) ackBits(4) len(2).
        public const int HeaderLen = 18;

        /// Maximum payload accepted from a peer.
        public const int MaxPayload = 1200;
        /// <summary>Reliable packets carry a 2-byte ordered-channel sequence before the payload.</summary>
        public const int ReliableOverhead = 2;

        /// Maximum length of any string field on the wire.
        public const int MaxString = 256;

        // ---- flags ----
        public const byte FlagCompressed = 1 << 0;
        public const byte FlagDelta = 1 << 1;
        public const byte FlagReliable = 1 << 2;
        public const byte FlagRetransmit = 1 << 3;
        public const byte FlagFragLast = 1 << 4;
    }

    public enum PacketKind : byte
    {
        Hello = 1,
        Welcome = 2,
        Ping = 3,
        Pong = 4,

        PlayerState = 10,
        PlayerLoadout = 11,
        CombatEvent = 12,
        Projectile = 13,
        EnemyState = 15,
        WorldEvent = 16,
        Chat = 17,

        Delta = 20,
        DesyncReport = 22,
        ResyncRequest = 23,

        Bye = 30,

        // Dread Delusion specific state channels.
        FactDelta = 40,
        QuestState = 41,
        ObjectState = 42,
        ClockState = 43,
        Cutscene = 44,
        Snapshot = 45,
        Fragment = 46,

        // Client -> host requests (the client acted; the host applies and re-broadcasts).
        FactRequest = 47,
        ObjectRequest = 48,
        WorldEventRequest = 49,

        // Shared experiences.
        Dialogue = 50,
        Airship = 51,
        AirshipState = 52,
        WorldAction = 53,
        CoopDeath = 54,
        FactoryState = 55,
    }

    public struct Header
    {
        public ushort Version;
        public PacketKind Kind;
        public byte Flags;
        public ushort Seq;
        public ushort Ack;
        public uint AckBits;
        public ushort PayloadLen;

        public bool HasFlag(byte f) => (Flags & f) != 0;

        public byte[] Encode()
        {
            var buf = new byte[Proto.HeaderLen];
            Buffer.BlockCopy(Proto.Magic, 0, buf, 0, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), Version);
            buf[6] = (byte)Kind;
            buf[7] = Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), Seq);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), Ack);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), AckBits);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), PayloadLen);
            return buf;
        }

        public static bool TryDecode(ReadOnlySpan<byte> src, out Header header)
        {
            header = default;
            if (src.Length < Proto.HeaderLen) return false;
            for (int i = 0; i < 4; i++)
                if (src[i] != Proto.Magic[i]) return false;

            header.Version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4));
            header.Kind = (PacketKind)src[6];
            header.Flags = src[7];
            header.Seq = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(8));
            header.Ack = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(10));
            header.AckBits = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12));
            header.PayloadLen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(16));
            return header.PayloadLen <= Proto.MaxPayload + Proto.ReliableOverhead;
        }
    }

    /// <summary>Minimal append-only writer. Strings are length-prefixed UTF-8 (max 256 B).</summary>
    public sealed class Writer
    {
        private readonly List<byte> _buf = new List<byte>(256);

        public int Length => _buf.Count;
        public byte[] ToArray() => _buf.ToArray();

        public Writer U8(byte v) { _buf.Add(v); return this; }
        public Writer Bool(bool v) { _buf.Add(v ? (byte)1 : (byte)0); return this; }

        public Writer U16(ushort v)
        {
            Span<byte> t = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(t, v);
            _buf.AddRange(t.ToArray());
            return this;
        }

        public Writer I32(int v)
        {
            Span<byte> t = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(t, v);
            _buf.AddRange(t.ToArray());
            return this;
        }

        public Writer I64(long v)
        {
            Span<byte> t = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(t, v);
            _buf.AddRange(t.ToArray());
            return this;
        }

        public Writer F32(float v) => I32(BitConverter.SingleToInt32Bits(v));

        public Writer Bytes(byte[] data)
        {
            if (data != null) _buf.AddRange(data);
            return this;
        }

        public Writer Str(string s)
        {
            if (s == null) { U16(0); return this; }
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if (bytes.Length > Proto.MaxString) Array.Resize(ref bytes, Proto.MaxString);
            U16((ushort)bytes.Length);
            _buf.AddRange(bytes);
            return this;
        }

        /// <summary>Quantised world position: 1 cm resolution.</summary>
        public Writer Position(UnityEngine.Vector3 p)
        {
            I32((int)Math.Round(p.x * 100f));
            I32((int)Math.Round(p.y * 100f));
            I32((int)Math.Round(p.z * 100f));
            return this;
        }

        /// <summary>Quantised yaw in 0.1 degree units, normalised to [0,3600).</summary>
        public Writer Yaw(float degrees)
        {
            int q = (int)Math.Round(degrees * 10f) % 3600;
            if (q < 0) q += 3600;
            return U16((ushort)q);
        }
    }

    /// <summary>Bounds-checked reader. Every read validates remaining length.</summary>
    public sealed class Reader
    {
        private readonly byte[] _buf;
        private int _pos;

        public Reader(byte[] buf) => _buf = buf;

        public int Remaining => _buf.Length - _pos;
        public bool Done => _pos >= _buf.Length;

        public byte U8()
        {
            if (Remaining < 1) throw new ProtocolException("truncated u8");
            return _buf[_pos++];
        }

        public bool Bool() => U8() != 0;

        public ushort U16()
        {
            if (Remaining < 2) throw new ProtocolException("truncated u16");
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        public int I32()
        {
            if (Remaining < 4) throw new ProtocolException("truncated i32");
            var v = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        public long I64()
        {
            if (Remaining < 8) throw new ProtocolException("truncated i64");
            var v = BinaryPrimitives.ReadInt64LittleEndian(_buf.AsSpan(_pos));
            _pos += 8;
            return v;
        }

        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public byte[] Bytes(int count)
        {
            if (count < 0 || Remaining < count) throw new ProtocolException("truncated bytes");
            var b = new byte[count];
            Buffer.BlockCopy(_buf, _pos, b, 0, count);
            _pos += count;
            return b;
        }

        public string Str()
        {
            int n = U16();
            if (n > Proto.MaxString || Remaining < n) throw new ProtocolException("bad string length");
            var s = System.Text.Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }

        public UnityEngine.Vector3 Position() => new UnityEngine.Vector3(
            I32() / 100f, I32() / 100f, I32() / 100f);

        public float Yaw() => U16() / 10f;
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }
}
