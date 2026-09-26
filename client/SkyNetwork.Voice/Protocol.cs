using System.Buffers.Binary;
using System.Text;

namespace SkyNetwork.Voice;

/// <summary>Datagram types of the SkyNetwork voice protocol (see skynet-voice, src/voice_server.h).</summary>
public enum PacketType : byte
{
    Auth = 1,
    AuthOk = 2,
    AuthFail = 3,
    Transceivers = 4,
    Audio = 5,
    AudioRx = 6,
    KeepAlive = 7,
    KeepAliveAck = 8,
    Bye = 9,
    Kick = 10,
}

/// <summary>
/// A radio antenna: a frequency at a place. Aircraft have one per radio; a controller one per radio and site,
/// with its coverage in <paramref name="RangeNm"/> (the server takes the larger of it and the radio horizon).
/// </summary>
public readonly record struct Transceiver(byte Id, uint FrequencyHz, double Latitude, double Longitude, double AltitudeFeet, double RangeNm = 0);

/// <summary>A frequency a received transmission arrived on and how strong it is there (0..1).</summary>
public readonly record struct RxFrequency(uint FrequencyHz, float Strength);

/// <summary>One Opus frame of someone's transmission, as the server relays it.</summary>
public sealed record AudioPacket(uint Sequence, bool Last, string Callsign, IReadOnlyList<RxFrequency> Frequencies, byte[] Opus);

/// <summary>A datagram from the server.</summary>
public sealed record ServerPacket(PacketType Type, uint Token = 0, string Reason = "", AudioPacket? Audio = null);

/// <summary>
/// Encoding and decoding of the voice datagrams. Every datagram starts with 'S' 'K' version type;
/// integers are big-endian, strings are a length byte and UTF-8 bytes, floats IEEE-754 big-endian.
/// </summary>
public static class Protocol
{
    public const byte Version = 1;
    public const int MaxDatagram = 1500;
    public const int MaxTransceivers = 8;

    public static byte[] Auth(uint cid, string callsign, string password)
    {
        var w = new Writer(PacketType.Auth);
        w.U32(cid);
        w.Str(callsign.ToUpperInvariant());
        w.Str(password);
        return w.ToArray();
    }

    public static byte[] Transceivers(uint token, IReadOnlyList<Transceiver> list)
    {
        if (list.Count > MaxTransceivers) throw new ArgumentException($"At most {MaxTransceivers} transceivers", nameof(list));
        var w = new Writer(PacketType.Transceivers);
        w.U32(token);
        w.U8((byte)list.Count);
        foreach (var t in list)
        {
            w.U8(t.Id);
            w.U32(t.FrequencyHz);
            w.F64(t.Latitude);
            w.F64(t.Longitude);
            w.F64(t.AltitudeFeet);
        }
        // Coverage after the list: servers before it read only the list and ignore the rest.
        if (list.Any(t => t.RangeNm > 0))
            foreach (var t in list) w.F32((float)Math.Clamp(t.RangeNm, 0, 10000));
        return w.ToArray();
    }

    public static byte[] Audio(uint token, uint sequence, bool last, ReadOnlySpan<byte> transmitterIds, ReadOnlySpan<byte> opus)
    {
        var w = new Writer(PacketType.Audio);
        w.U32(token);
        w.U32(sequence);
        w.U8(last ? (byte)1 : (byte)0);
        w.U8((byte)transmitterIds.Length);
        w.Bytes(transmitterIds);
        w.Bytes(opus);
        return w.ToArray();
    }

    public static byte[] KeepAlive(uint token) => TokenOnly(PacketType.KeepAlive, token);
    public static byte[] Bye(uint token) => TokenOnly(PacketType.Bye, token);

    private static byte[] TokenOnly(PacketType type, uint token)
    {
        var w = new Writer(type);
        w.U32(token);
        return w.ToArray();
    }

    /// <summary>Parses a datagram from the server; null for anything malformed or unknown.</summary>
    public static ServerPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 'S' || data[1] != 'K' || data[2] != Version) return null;
        var type = (PacketType)data[3];
        var r = new Reader(data[4..]);
        ServerPacket? packet = type switch
        {
            PacketType.AuthOk => new ServerPacket(type, Token: r.U32()),
            PacketType.AuthFail or PacketType.Kick => new ServerPacket(type, Reason: r.Str()),
            PacketType.KeepAliveAck => new ServerPacket(type),
            PacketType.AudioRx => ParseAudio(ref r),
            _ => null,
        };
        return r.Ok ? packet : null;
    }

    private static ServerPacket ParseAudio(ref Reader r)
    {
        uint seq = r.U32();
        bool last = r.U8() != 0;
        string callsign = r.Str();
        int n = r.U8();
        var freqs = new RxFrequency[Math.Min(n, 64)];
        for (int i = 0; i < n; i++)
        {
            var f = new RxFrequency(r.U32(), r.F32());
            if (i < freqs.Length) freqs[i] = f;
        }
        return new ServerPacket(PacketType.AudioRx, Audio: new AudioPacket(seq, last, callsign, freqs, r.Rest()));
    }

    private sealed class Writer
    {
        private readonly MemoryStream _s = new();

        public Writer(PacketType type)
        {
            _s.WriteByte((byte)'S');
            _s.WriteByte((byte)'K');
            _s.WriteByte(Version);
            _s.WriteByte((byte)type);
        }

        public void U8(byte v) => _s.WriteByte(v);
        public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            _s.Write(b);
        }

        public void F32(float v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(b, v);
            _s.Write(b);
        }

        public void F64(double v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, v);
            _s.Write(b);
        }

        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = Math.Min(bytes.Length, 255);
            _s.WriteByte((byte)n);
            _s.Write(bytes, 0, n);
        }

        public byte[] ToArray() => _s.ToArray();
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _d = data;
        public bool Ok { get; private set; } = true;

        private bool Need(int n)
        {
            if (_d.Length < n) Ok = false;
            return Ok;
        }

        public byte U8()
        {
            if (!Need(1)) return 0;
            byte v = _d[0];
            _d = _d[1..];
            return v;
        }

        public uint U32()
        {
            if (!Need(4)) return 0;
            uint v = BinaryPrimitives.ReadUInt32BigEndian(_d);
            _d = _d[4..];
            return v;
        }

        public float F32()
        {
            if (!Need(4)) return 0;
            float v = BinaryPrimitives.ReadSingleBigEndian(_d);
            _d = _d[4..];
            return v;
        }

        public string Str()
        {
            int n = U8();
            if (!Need(n)) return "";
            string s = Encoding.UTF8.GetString(_d[..n]);
            _d = _d[n..];
            return s;
        }

        public byte[] Rest()
        {
            var b = _d.ToArray();
            _d = default;
            return b;
        }
    }
}
