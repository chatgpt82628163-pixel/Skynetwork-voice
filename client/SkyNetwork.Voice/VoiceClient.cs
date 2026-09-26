using NAudio.Wave;

namespace SkyNetwork.Voice;

/// <summary>A radio the user has: a frequency with receive and transmit switches.</summary>
public sealed record Radio(uint FrequencyHz, bool Receive = true, bool Transmit = false, float Volume = 1)
{
    /// <summary>"118.100" (MHz, three decimals) to Hz; 0 if not a frequency.</summary>
    public static uint ParseMhz(string text) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var mhz) && mhz is >= 100 and < 1000
            ? (uint)Math.Round(mhz * 1_000_000m) : 0;

    public static string FormatMhz(uint hz) => (hz / 1_000_000m).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Where the antennas are: the aircraft, or a controller's sites. <paramref name="RangeNm"/> is a
/// controller's coverage around the site (0: only the radio horizon counts).
/// </summary>
public readonly record struct AntennaSite(double Latitude, double Longitude, double AltitudeFeet, double RangeNm = 0);

public enum VoiceState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed class VoiceSettings
{
    /// <summary>Audio devices by index (-1 = Windows default).</summary>
    public int InputDevice { get; set; } = -1;
    public int OutputDevice { get; set; } = -1;
    public float MicGain { get; set; } = 1;
    public float OutputVolume { get; set; } = 1;
    public PttBinding Ptt { get; set; } = PttBinding.None;
    /// <summary>Radio noise (hiss, squelch tail) on what we hear; off gives a clean voice.</summary>
    public bool RadioNoise { get; set; } = true;
}

/// <summary>
/// Everything a pilot or controller client needs for radio voice: the connection, the radios and
/// their antennas, microphone and speakers, push-to-talk. The client tells it its radios and
/// position; it keeps the server's transceivers up to date.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    private readonly object _lock = new();
    private readonly PushToTalk _ptt = new();
    private VoiceConnection? _connection;
    private Transmitter? _transmitter;
    private RadioMixer? _mixer;
    private WaveInEvent? _mic;
    private WaveOutEvent? _speakers;
    private IReadOnlyList<Radio> _radios = [];
    private IReadOnlyList<AntennaSite> _sites = [];
    private IReadOnlyList<Transceiver> _transceivers = [];
    private bool _manualPtt;
    private VoiceSettings _settings = new();

    public VoiceClient()
    {
        _ptt.Changed += _ => UpdateKey();
    }

    public VoiceState State { get; private set; }
    public bool Transmitting => _transmitter?.Transmitting == true;
    public float MicLevel => _transmitter?.Level ?? 0;
    public IReadOnlyList<Radio> Radios => _radios;

    /// <summary>Connection state changed; the string is an error or kick reason when it went down.</summary>
    public event Action<VoiceState, string>? StateChanged;

    /// <summary>A station started (true) or stopped (false) being heard on a frequency.</summary>
    public event Action<string, uint, bool>? ReceiveActivity;

    /// <summary>We started (true) or stopped (false) transmitting.</summary>
    public event Action<bool>? TransmitChanged;

    public void ApplySettings(VoiceSettings settings)
    {
        _settings = settings;
        _ptt.Binding = settings.Ptt;
        if (_transmitter != null) _transmitter.Gain = settings.MicGain;
        if (_mixer != null)
        {
            _mixer.MasterVolume = settings.OutputVolume;
            _mixer.RadioNoise = settings.RadioNoise;
        }
        if (State == VoiceState.Connected) RestartAudio();
    }

    public async Task ConnectAsync(string host, int port, uint cid, string callsign, string password, CancellationToken ct = default)
    {
        Disconnect();
        SetState(VoiceState.Connecting, "");
        var connection = new VoiceConnection(host, port);
        try
        {
            await connection.ConnectAsync(cid, callsign, password, ct);
        }
        catch (Exception ex) when (ex is VoiceException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            connection.Dispose();
            SetState(VoiceState.Disconnected, ex.Message);
            throw;
        }
        lock (_lock)
        {
            _connection = connection;
            _transmitter = new Transmitter(connection) { Gain = _settings.MicGain };
            _mixer = new RadioMixer(ReceiveVolume) { MasterVolume = _settings.OutputVolume, RadioNoise = _settings.RadioNoise };
            _mixer.Activity += (cs, f, on) => ReceiveActivity?.Invoke(cs, f, on);
            connection.AudioReceived += p => _mixer?.Add(p);
            connection.Closed += reason =>
            {
                StopAudio();
                SetState(VoiceState.Disconnected, reason);
            };
        }
        PublishTransceivers();
        StartAudio();
        SetState(VoiceState.Connected, "");
    }

    public void Disconnect()
    {
        VoiceConnection? c;
        lock (_lock)
        {
            c = _connection;
            _connection = null;
        }
        if (c == null) return;
        StopAudio();
        c.Dispose();
        _transmitter = null;
        _mixer = null;
        SetState(VoiceState.Disconnected, "");
    }

    /// <summary>The user's radios (pilot: COM1, COM2; controller: primary and other frequencies).</summary>
    public void SetRadios(IReadOnlyList<Radio> radios)
    {
        if (radios.SequenceEqual(_radios)) return;
        _radios = radios.ToList();
        PublishTransceivers();
        UpdateKey();
    }

    /// <summary>Antenna positions: one for an aircraft, one or more sites for a controller.</summary>
    public void SetSites(IReadOnlyList<AntennaSite> sites)
    {
        // Small movements do not matter for radio range: avoid flooding the server.
        if (sites.Count == _sites.Count && sites.Zip(_sites).All(p =>
                Math.Abs(p.First.Latitude - p.Second.Latitude) < 0.01 && Math.Abs(p.First.Longitude - p.Second.Longitude) < 0.01 &&
                Math.Abs(p.First.AltitudeFeet - p.Second.AltitudeFeet) < 200))
            return;
        _sites = sites.ToList();
        PublishTransceivers();
    }

    /// <summary>Push-to-talk from the user interface (a button), in addition to the bound key.</summary>
    public void SetManualPtt(bool down)
    {
        _manualPtt = down;
        UpdateKey();
    }

    /// <summary>Transceivers for the server: every radio with a frequency, at every site (at most 8).</summary>
    internal static IReadOnlyList<Transceiver> BuildTransceivers(IReadOnlyList<Radio> radios, IReadOnlyList<AntennaSite> sites,
        out IReadOnlyList<byte> transmitIds)
    {
        var list = new List<Transceiver>();
        var tx = new List<byte>();
        foreach (var radio in radios.Where(r => r.FrequencyHz > 0))
            foreach (var site in sites)
            {
                if (list.Count == Protocol.MaxTransceivers) break;
                byte id = (byte)list.Count;
                list.Add(new Transceiver(id, radio.FrequencyHz, site.Latitude, site.Longitude, site.AltitudeFeet, site.RangeNm));
                if (radio.Transmit) tx.Add(id);
            }
        transmitIds = tx;
        return list;
    }

    private void PublishTransceivers()
    {
        _transceivers = BuildTransceivers(_radios, _sites, out var tx);
        _transmitter?.SetTransmitters(tx);
        _connection?.SetTransceivers(_transceivers);
    }

    private float ReceiveVolume(uint freq)
    {
        bool transmitting = Transmitting;
        float best = 0;
        foreach (var r in _radios)
        {
            if (r.FrequencyHz != freq || !r.Receive) continue;
            // A radio cannot receive while it transmits.
            if (transmitting && r.Transmit) return 0;
            best = Math.Max(best, r.Volume);
        }
        return best;
    }

    private void UpdateKey()
    {
        var t = _transmitter;
        if (t == null) return;
        bool before = t.Transmitting;
        t.Key(_manualPtt || _ptt.IsDown);
        if (t.Transmitting != before) TransmitChanged?.Invoke(t.Transmitting);
    }

    private void StartAudio()
    {
        if (!OperatingSystem.IsWindows()) return; // audio devices: Windows only (tests run without them)
        try
        {
            _speakers = new WaveOutEvent { DeviceNumber = _settings.OutputDevice, DesiredLatency = 120, NumberOfBuffers = 3 };
            _speakers.Init(_mixer!, convertTo16Bit: true);
            _speakers.Play();
            _mic = new WaveInEvent
            {
                DeviceNumber = _settings.InputDevice,
                WaveFormat = new WaveFormat(AudioFormat.SampleRate, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 4,
            };
            _mic.DataAvailable += (_, e) => _transmitter?.AddPcm16(e.Buffer.AsSpan(0, e.BytesRecorded));
            _mic.StartRecording();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any device failure (unplugged, busy, unsupported format) leaves voice without audio, never crashes the client.
            StopAudio();
            SetState(State, "Audio device error: " + ex.Message);
        }
    }

    private void StopAudio()
    {
        try { _mic?.StopRecording(); } catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException) { }
        _mic?.Dispose();
        _mic = null;
        try { _speakers?.Stop(); } catch (NAudio.MmException) { }
        _speakers?.Dispose();
        _speakers = null;
        _mixer?.Clear();
    }

    private void RestartAudio()
    {
        StopAudio();
        StartAudio();
    }

    private void SetState(VoiceState state, string reason)
    {
        State = state;
        StateChanged?.Invoke(state, reason);
    }

    /// <summary>Microphones and speakers by index, for the settings window.</summary>
    public static (IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs) Devices()
    {
        if (!OperatingSystem.IsWindows()) return ([], []);
        var inputs = Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();
        var outputs = Enumerable.Range(0, WaveInterop.waveOutGetNumDevs()).Select(i =>
        {
            var caps = new WaveOutCapabilities();
            WaveInterop.waveOutGetDevCaps(i, out caps, System.Runtime.InteropServices.Marshal.SizeOf(caps));
            return caps.ProductName;
        }).ToList();
        return (inputs, outputs);
    }

    public void Dispose()
    {
        Disconnect();
        _ptt.Dispose();
    }
}
