using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SkyNetwork.Voice.Tests;

internal static class Signal
{
    public static float[] Sine(double hz, int samples, float amplitude = 0.5f, int offset = 0) =>
        Enumerable.Range(offset, samples).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * hz * i / AudioFormat.SampleRate)).ToArray();

    public static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float x in s) sum += x * x;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    public static List<byte[]> EncodeSine(double hz, int frames)
    {
        var enc = new VoiceEncoder();
        return Enumerable.Range(0, frames).Select(f => enc.Encode(Sine(hz, AudioFormat.FrameSamples, offset: f * AudioFormat.FrameSamples))).ToList();
    }
}

public class ProtocolTests
{
    [Fact]
    public void AuthLayout()
    {
        var b = Protocol.Auth(1234, "afl123", "pw");
        Assert.Equal(new byte[] { (byte)'S', (byte)'K', 1, 1, 0, 0, 4, 210, 6, (byte)'A', (byte)'F', (byte)'L', (byte)'1', (byte)'2', (byte)'3', 2, (byte)'p', (byte)'w' }, b);
    }

    [Fact]
    public void ParsesAudioAndRejectsGarbage()
    {
        var d = new List<byte> { (byte)'S', (byte)'K', 1, 6 };
        d.AddRange(new byte[] { 0, 0, 0, 7, 1, 4 });
        d.AddRange("UUEE"u8.ToArray());
        d.Add(1);
        var f = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(f, 118_100_000);
        BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(4), 0.75f);
        d.AddRange(f);
        d.AddRange(new byte[] { 9, 9, 9 });
        var p = Protocol.Parse(d.ToArray())!;
        Assert.Equal(PacketType.AudioRx, p.Type);
        Assert.Equal((7u, true, "UUEE"), (p.Audio!.Sequence, p.Audio.Last, p.Audio.Callsign));
        Assert.Equal(new RxFrequency(118_100_000, 0.75f), Assert.Single(p.Audio.Frequencies));
        Assert.Equal(new byte[] { 9, 9, 9 }, p.Audio.Opus);

        Assert.Null(Protocol.Parse(d.Take(12).ToArray()));
        Assert.Null(Protocol.Parse("XK\x01\x06"u8.ToArray()));
    }

    [Theory]
    [InlineData("118.100", 118_100_000u)]
    [InlineData("121,5", 121_500_000u)]
    [InlineData("118.005", 118_005_000u)]
    [InlineData("abc", 0u)]
    [InlineData("11.8", 0u)]
    public void ParsesFrequencies(string text, uint hz) => Assert.Equal(hz, Radio.ParseMhz(text));

    [Fact]
    public void PttBindingsRoundTrip()
    {
        Assert.Equal(new PttBinding(PttKind.Keyboard, 0xA3), PttBinding.Parse("key:163"));
        Assert.Equal(new PttBinding(PttKind.Joystick, 4, 1), PttBinding.Parse("joy:1:4"));
        Assert.Equal("joy:1:4", PttBinding.Parse("joy:1:4").ToString());
        Assert.Equal(PttBinding.None, PttBinding.Parse("nonsense"));
        Assert.Equal("Right Ctrl", PttBinding.Parse("key:163").Describe());
        Assert.Equal("Joystick 2 button 5", PttBinding.Parse("joy:1:4").Describe());
    }
}

public class AudioTests
{
    [Fact]
    public void OpusRoundTripKeepsTheSignal()
    {
        var frames = Signal.EncodeSine(1000, 10);
        Assert.All(frames, f => Assert.InRange(f.Length, 10, 200)); // ~24 kbit/s
        var dec = new VoiceDecoder();
        var output = new float[AudioFormat.FrameSamples];
        foreach (var f in frames) dec.Decode(f, output);
        Assert.InRange(Signal.Rms(output), 0.25, 0.45); // the 0.5 sine has RMS 0.35
    }

    [Fact]
    public void RadioEffectIsABandPass_AndWeakSignalsAreNoisier()
    {
        static double Through(double hz, float strength)
        {
            var e = new RadioEffect { Strength = strength };
            var s = Signal.Sine(hz, 9600, 0.2f);
            e.Process(s);
            return Signal.Rms(s.AsSpan(4800));
        }
        Assert.True(Through(1000, 1) > 3 * Through(80, 1), "low frequencies are cut");
        Assert.True(Through(1000, 1) > 3 * Through(9000, 1), "high frequencies are cut");
        Assert.True(RadioEffect.NoiseLevel(0.1f) > 5 * RadioEffect.NoiseLevel(1f));
    }

    [Fact]
    public void RadioNoiseIsFaint_AndCanBeSwitchedOff()
    {
        // Silence in: only the hiss comes out, and it stays far below speech (RMS ~0.1-0.3).
        var hiss = new float[9600];
        new RadioEffect { Strength = 0.2f }.Process(hiss);
        Assert.InRange(Signal.Rms(hiss), 0.0001, 0.01);

        var quiet = new float[9600];
        var off = new RadioEffect { Strength = 0.2f, Noise = false };
        off.Process(quiet);
        Assert.Equal(0, Signal.Rms(quiet));
        var tail = new float[960];
        off.SquelchTail(tail, 0, tail.Length);
        Assert.All(tail, x => Assert.Equal(0, x));
    }

    [Fact]
    public void TransceiversCarryTheControllersCoverage()
    {
        var plain = Protocol.Transceivers(7, [new Transceiver(0, 118_100_000, 55.9, 37.4, 100)]);
        var wide = Protocol.Transceivers(7, [new Transceiver(0, 118_100_000, 55.9, 37.4, 100, 1000)]);
        Assert.Equal(plain.Length + 4, wide.Length);  // an old server reads the same list and stops
        Assert.Equal(1000f, System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(wide.AsSpan(wide.Length - 4)));
    }

    private static AudioPacket Packet(uint seq, byte[] opus, bool last = false, float strength = 0.9f, uint freq = 118_100_000) =>
        new(seq, last, "AFL123", [new RxFrequency(freq, strength)], opus);

    private static float[] Read(RadioMixer m, int ms)
    {
        var buf = new float[AudioFormat.SampleRate * ms / 1000];
        m.Read(buf, 0, buf.Length);
        return buf;
    }

    [Fact]
    public void MixerPlaysATransmission_ReportsActivity_AndCloses()
    {
        var events = new List<(string, uint, bool)>();
        var mixer = new RadioMixer(f => f == 118_100_000 ? 1 : 0);
        mixer.Activity += (c, f, on) => events.Add((c, f, on));
        var frames = Signal.EncodeSine(800, 25);
        for (int i = 0; i < frames.Count; i++) mixer.Add(Packet((uint)(100 + i), frames[i], last: i == frames.Count - 1));

        var audio = Read(mixer, 400);
        Assert.True(Signal.Rms(audio.AsSpan(4800)) > 0.1, "the transmission is heard");
        Assert.Equal(("AFL123", 118_100_000u, true), events[0]);
        Read(mixer, 400); // rest of it, the squelch tail and the end
        Assert.Equal(("AFL123", 118_100_000u, false), events[^1]);
        Assert.Empty(mixer.Active);
        Assert.True(Signal.Rms(Read(mixer, 100)) < 1e-6, "silence afterwards");
    }

    [Fact]
    public void MixerIgnoresFrequenciesWeDoNotListenTo()
    {
        var mixer = new RadioMixer(f => f == 121_500_000 ? 1 : 0);
        foreach (var (f, i) in Signal.EncodeSine(800, 10).Select((f, i) => (f, i))) mixer.Add(Packet((uint)i, f));
        Assert.True(Signal.Rms(Read(mixer, 200)) < 1e-6);
    }

    [Fact]
    public void MixerSurvivesLossAndReordering()
    {
        var mixer = new RadioMixer(_ => 1);
        var frames = Signal.EncodeSine(800, 30);
        var order = Enumerable.Range(0, 30).Where(i => i != 12 && i != 13).ToList();  // two lost
        (order[5], order[6]) = (order[6], order[5]);                                   // one swapped
        foreach (int i in order) mixer.Add(Packet((uint)i, frames[i], last: i == 29));
        var audio = Read(mixer, 700);
        // Heard throughout, including across the gap (concealed), then closed.
        for (int ms = 100; ms < 560; ms += 60)
            Assert.True(Signal.Rms(audio.AsSpan(ms * 48, 960)) > 0.02, $"audible at {ms} ms");
        Assert.Empty(mixer.Active);
    }

    [Fact]
    public void TwoStationsAreMixed()
    {
        var mixer = new RadioMixer(_ => 1);
        var a = Signal.EncodeSine(600, 10);
        var b = Signal.EncodeSine(1200, 10);
        for (int i = 0; i < 10; i++)
        {
            mixer.Add(Packet((uint)i, a[i]));
            mixer.Add(new AudioPacket((uint)(50 + i), false, "SBI2", [new RxFrequency(118_100_000, 0.9f)], b[i]));
        }
        Read(mixer, 100);
        Assert.Equal(2, mixer.Active.Count);
    }
}

internal sealed class FakeSender : IAudioSender
{
    public readonly List<(uint Seq, bool Last, byte[] Tx, int Bytes)> Sent = [];
    public bool IsConnected { get; set; } = true;
    public void SendAudio(uint sequence, bool last, ReadOnlySpan<byte> transmitterIds, ReadOnlySpan<byte> opus) =>
        Sent.Add((sequence, last, transmitterIds.ToArray(), opus.Length));
}

public class TransmitterTests
{
    [Fact]
    public void SendsFramesWhileKeyed_AndClosesTheTransmission()
    {
        var sender = new FakeSender();
        var t = new Transmitter(sender);
        t.SetTransmitters([0, 2]);
        t.AddSamples(Signal.Sine(500, 960 * 2)); // not keyed: nothing goes out
        Assert.Empty(sender.Sent);
        t.Key(true);
        t.AddSamples(Signal.Sine(500, 960 * 3 + 100));
        t.Key(false);
        Assert.Equal([(0u, false), (1u, false), (2u, false), (3u, true)], sender.Sent.Select(s => (s.Seq, s.Last)));
        Assert.All(sender.Sent, s => Assert.Equal(new byte[] { 0, 2 }, s.Tx));
        Assert.True(t.Level > 0.4);
    }

    [Fact]
    public void NoTransmitRadio_NoTransmission()
    {
        var sender = new FakeSender();
        var t = new Transmitter(sender);
        t.Key(true);
        t.AddSamples(Signal.Sine(500, 960 * 2));
        t.Key(false);
        Assert.Empty(sender.Sent);
        Assert.False(t.Transmitting);
    }

    [Fact]
    public void TransceiversForRadiosAndSites()
    {
        var radios = new[] { new Radio(118_100_000, Transmit: true), new Radio(121_500_000), new Radio(0) };
        var sites = new[] { new AntennaSite(55, 37, 100), new AntennaSite(56, 38, 100) };
        var list = VoiceClient.BuildTransceivers(radios, sites, out var tx);
        Assert.Equal(4, list.Count);
        Assert.Equal(new byte[] { 0, 1 }, tx);
        Assert.Equal(new uint[] { 118_100_000, 118_100_000, 121_500_000, 121_500_000 }, list.Select(t => t.FrequencyHz));
        var many = VoiceClient.BuildTransceivers(radios, Enumerable.Repeat(sites[0], 6).ToArray(), out _);
        Assert.Equal(Protocol.MaxTransceivers, many.Count);
    }
}

/// <summary>
/// Against the real skynet-voice server (built in ../../build, or SKYNET_VOICE_BUILD). Skipped
/// quietly when the binaries are not there.
/// </summary>
public sealed class ServerTests : IDisposable
{
    private readonly string? _build;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"voice-test-{Guid.NewGuid():N}.db");
    private Process? _server;
    private int _port;

    public ServerTests()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_VOICE_BUILD");
        if (string.IsNullOrEmpty(dir))
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "build")) ) d = d.Parent;
            dir = d == null ? null : Path.Combine(d.FullName, "build");
        }
        if (dir != null && File.Exists(Path.Combine(dir, "skynet-voice"))) _build = dir;
    }

    private void Admin(params string[] args)
    {
        var p = Process.Start(new ProcessStartInfo(Path.Combine(_build!, "skynet-admin"), ["--db", _db, .. args]) { RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private bool Start()
    {
        if (_build == null) return false;
        Admin("adduser", "1", "Pilot One", "pw1");
        Admin("adduser", "2", "Tower", "pw2", "S2");
        using (var probe = new UdpClient(0)) _port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        _server = Process.Start(new ProcessStartInfo(Path.Combine(_build, "skynet-voice"),
            ["--db", _db, "--host", "127.0.0.1", "--port", _port.ToString(), "--account-check", "1"]) { RedirectStandardError = true });
        Thread.Sleep(300);
        return true;
    }

    [Fact]
    public async Task TwoStationsTalk_WrongPasswordIsRefused_SuspensionKicks()
    {
        if (!Start()) return;

        var bad = new VoiceConnection("127.0.0.1", _port);
        var ex = await Assert.ThrowsAsync<VoiceException>(() => bad.ConnectAsync(1, "AFL1", "nope"));
        Assert.Equal("invalid credentials", ex.Message);

        using var pilot = new VoiceConnection("127.0.0.1", _port);
        using var tower = new VoiceConnection("127.0.0.1", _port);
        await pilot.ConnectAsync(1, "afl1", "pw1");
        await tower.ConnectAsync(2, "UUEE_TWR", "pw2");
        pilot.SetTransceivers([new Transceiver(0, 118_100_000, 56.1, 37.4, 3000)]);
        tower.SetTransceivers([new Transceiver(0, 118_100_000, 55.97, 37.41, 100)]);
        await Task.Delay(200);

        var got = new TaskCompletionSource<AudioPacket>();
        tower.AudioReceived += p => got.TrySetResult(p);
        var frame = Signal.EncodeSine(700, 1)[0];
        pilot.SendAudio(42, false, [0], frame);
        var packet = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(("AFL1", 42u), (packet.Callsign, packet.Sequence));
        Assert.Equal(frame, packet.Opus);
        var rx = Assert.Single(packet.Frequencies);
        Assert.Equal(118_100_000u, rx.FrequencyHz);
        Assert.InRange(rx.Strength, 0.5f, 1f);

        var closed = new TaskCompletionSource<string>();
        pilot.Closed += r => closed.TrySetResult(r);
        Admin("suspend", "1");
        Assert.Equal("CID suspended", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(pilot.IsConnected);
    }

    public void Dispose()
    {
        if (_server is { HasExited: false }) _server.Kill();
        _server?.Dispose();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
