using NAudio.Wave;

namespace SkyNetwork.Voice;

/// <summary>
/// Everything heard on the radios, mixed into one output: one stream per transmitting station,
/// each with a small jitter buffer, Opus decoding with loss concealment, the radio effect for its
/// signal strength and a squelch tail when it ends. Read by the audio device on its own thread.
/// </summary>
public sealed class RadioMixer : ISampleProvider
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RxStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private float[] _scratch = new float[4096];

    /// <param name="receiveVolume">
    /// Volume (0..1) for a frequency; 0 means not listening to it (radio off, receive disabled or
    /// blocked by our own transmission).
    /// </param>
    public RadioMixer(Func<uint, float> receiveVolume) => ReceiveVolume = receiveVolume;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 1);
    public Func<uint, float> ReceiveVolume { get; set; }
    public float MasterVolume { get; set; } = 1;
    /// <summary>Receiver hiss and squelch tail on received transmissions (the voice filter stays).</summary>
    public bool RadioNoise { get; set; } = true;

    /// <summary>Someone started (true) or stopped (false) being heard on a frequency. Raised on the audio thread.</summary>
    public event Action<string, uint, bool>? Activity;

    /// <summary>Callsigns heard right now with their frequency.</summary>
    public IReadOnlyList<(string Callsign, uint FrequencyHz)> Active
    {
        get
        {
            lock (_lock) return _streams.Values.Where(s => s.Audible).Select(s => (s.Callsign, s.FrequencyHz)).ToList();
        }
    }

    /// <summary>Takes a frame from the network.</summary>
    public void Add(AudioPacket packet)
    {
        // The frequency we hear it best on, among those we listen to.
        uint freq = 0;
        float strength = 0, volume = 0;
        foreach (var f in packet.Frequencies)
        {
            float v = ReceiveVolume(f.FrequencyHz);
            if (v <= 0 || f.Strength <= strength) continue;
            (freq, strength, volume) = (f.FrequencyHz, f.Strength, v);
        }
        if (freq == 0) return;
        lock (_lock)
        {
            if (!_streams.TryGetValue(packet.Callsign, out var s) || s.Finished)
                _streams[packet.Callsign] = s = new RxStream(packet.Callsign);
            s.Add(packet, freq, strength, volume, RadioNoise);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var output = buffer.AsSpan(offset, count);
        output.Clear();
        List<(string, uint, bool)>? events = null;
        lock (_lock)
        {
            if (_scratch.Length < count) _scratch = new float[count];
            var temp = _scratch.AsSpan(0, count);
            foreach (var s in _streams.Values)
            {
                bool wasAudible = s.Audible;
                s.Read(temp);
                float v = s.Volume * MasterVolume;
                for (int i = 0; i < count; i++) output[i] += temp[i] * v;
                if (s.Audible != wasAudible) (events ??= []).Add((s.Callsign, s.FrequencyHz, s.Audible));
            }
            foreach (var done in _streams.Where(kv => kv.Value.Finished).Select(kv => kv.Key).ToList()) _streams.Remove(done);
        }
        for (int i = 0; i < count; i++) output[i] = Math.Clamp(output[i], -1f, 1f);
        if (events != null)
            foreach (var (cs, f, on) in events) Activity?.Invoke(cs, f, on);
        return count; // an audio output never runs dry
    }

    /// <summary>Stops everything being heard (radio switched off, disconnected).</summary>
    public void Clear()
    {
        lock (_lock) _streams.Clear();
    }
}

/// <summary>One station's transmission as we receive it.</summary>
internal sealed class RxStream(string callsign)
{
    private const int Prebuffer = 3;           // 60 ms of jitter protection
    private const int MaxConcealed = 5;        // 100 ms of loss before we give up
    private const int TailSamples = AudioFormat.SampleRate * 70 / 1000;

    private enum State { Buffering, Playing, Tail, Finished }

    private readonly SortedDictionary<uint, byte[]> _frames = [];
    private readonly VoiceDecoder _decoder = new();
    private readonly RadioEffect _effect = new();
    private readonly float[] _frame = new float[AudioFormat.FrameSamples];
    private int _framePos = AudioFormat.FrameSamples;
    private State _state;
    private uint _next;
    private uint? _endSeq;
    private int _concealed, _tailLeft, _bufferingFrames;

    public string Callsign { get; } = callsign;
    public uint FrequencyHz { get; private set; }
    public float Volume { get; private set; }
    public bool Audible => _state == State.Playing;
    public bool Finished => _state == State.Finished;

    public void Add(AudioPacket p, uint freq, float strength, float volume, bool noise = true)
    {
        _effect.Noise = noise;
        if (_state is State.Tail or State.Finished) return;
        if (_state == State.Playing && (int)(p.Sequence - _next) < 0) return; // too late, already concealed
        _frames[p.Sequence] = p.Opus;
        if (p.Last) _endSeq = p.Sequence;
        FrequencyHz = freq;
        Volume = volume;
        _effect.Strength = strength;
    }

    public void Read(Span<float> output)
    {
        int written = 0;
        while (written < output.Length)
        {
            if (_framePos >= _frame.Length && !NextFrame())
            {
                output[written..].Clear();
                return;
            }
            int n = Math.Min(_frame.Length - _framePos, output.Length - written);
            _frame.AsSpan(_framePos, n).CopyTo(output[written..]);
            _framePos += n;
            written += n;
        }
    }

    /// <summary>Produces the next 20 ms into <see cref="_frame"/>; false when the stream is over.</summary>
    private bool NextFrame()
    {
        _framePos = 0;
        switch (_state)
        {
            case State.Buffering:
                // Wait for a few frames (or the end of a very short transmission), at most ~100 ms.
                if (_frames.Count >= Prebuffer || _endSeq != null || ++_bufferingFrames > 5)
                {
                    _state = State.Playing;
                    if (_frames.Count > 0) _next = _frames.Keys.First();
                    return NextFrame();
                }
                Array.Clear(_frame);
                return true;

            case State.Playing:
                if (_frames.Remove(_next, out var opus))
                {
                    _decoder.Decode(opus, _frame);
                    _concealed = 0;
                }
                else if (_endSeq is { } end && (int)(_next - end) > 0)
                {
                    StartTail();
                    return true;
                }
                else if (_frames.Count > 0 && _concealed >= MaxConcealed)
                {
                    // A long gap: jump to what we have.
                    _next = _frames.Keys.First();
                    _concealed = 0;
                    return NextFrame();
                }
                else if (_concealed < MaxConcealed)
                {
                    _decoder.Decode(null, _frame);
                    _concealed++;
                }
                else
                {
                    StartTail(); // the transmission stopped arriving
                    return true;
                }
                _next++;
                _effect.Process(_frame);
                return true;

            case State.Tail:
                if (_tailLeft <= 0)
                {
                    _state = State.Finished;
                    return false;
                }
                int n = Math.Min(_tailLeft, _frame.Length);
                _effect.SquelchTail(_frame.AsSpan(0, n), TailSamples - _tailLeft, TailSamples);
                _frame.AsSpan(n).Clear();
                _tailLeft -= n;
                return true;

            default:
                return false;
        }
    }

    private void StartTail()
    {
        _state = State.Tail;
        _tailLeft = TailSamples;
        _frames.Clear();
        NextFrame();
    }
}
