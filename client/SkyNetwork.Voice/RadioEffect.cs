namespace SkyNetwork.Voice;

/// <summary>Second-order IIR filter (RBJ cookbook).</summary>
internal struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad HighPass(double freq, double q = 0.707) => Make(freq, q, highPass: true);
    public static Biquad LowPass(double freq, double q = 0.707) => Make(freq, q, highPass: false);

    private static Biquad Make(double freq, double q, bool highPass)
    {
        double w = 2 * Math.PI * freq / AudioFormat.SampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double a0 = 1 + alpha;
        double b0 = highPass ? (1 + cos) / 2 : (1 - cos) / 2;
        double b1 = highPass ? -(1 + cos) : 1 - cos;
        return new Biquad
        {
            _b0 = (float)(b0 / a0), _b1 = (float)(b1 / a0), _b2 = (float)(b0 / a0),
            _a1 = (float)(-2 * cos / a0), _a2 = (float)((1 - alpha) / a0),
        };
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}

/// <summary>
/// Makes clean audio sound like a VHF airband receiver: 300–3000 Hz band-pass, a little
/// compression and drive, and a faint hiss that grows as the signal gets weaker (far away, low
/// altitude). The hiss and the squelch tail can be switched off (<see cref="Noise"/>).
/// </summary>
internal sealed class RadioEffect
{
    private Biquad _hp1 = Biquad.HighPass(300), _hp2 = Biquad.HighPass(300);
    private Biquad _lp1 = Biquad.LowPass(3000), _lp2 = Biquad.LowPass(3000);
    private Biquad _noiseBand = Biquad.LowPass(4000);
    private readonly Random _random = new();

    /// <summary>Signal strength 0..1 as reported by the server.</summary>
    public float Strength { get; set; } = 1;

    /// <summary>Receiver hiss and the squelch tail; off leaves only the band-pass voice.</summary>
    public bool Noise { get; set; } = true;

    /// <summary>Kept just audible: a strong signal is almost clean, a weak one only a little noisy.</summary>
    public static float NoiseLevel(float strength) => 0.0015f + 0.02f * MathF.Pow(1 - Math.Clamp(strength, 0, 1), 2);

    public const float SquelchLevel = 0.025f;

    public void Process(Span<float> samples)
    {
        float noise = Noise ? NoiseLevel(Strength) : 0;
        // Weak signals also fade a little.
        float gain = 1.6f * (0.55f + 0.45f * MathF.Sqrt(Math.Clamp(Strength, 0, 1)));
        for (int i = 0; i < samples.Length; i++)
        {
            float x = _lp2.Process(_lp1.Process(_hp2.Process(_hp1.Process(samples[i])))) * gain;
            x = MathF.Tanh(x * 1.4f) / 1.1f;                       // drive and soft limit
            x += _noiseBand.Process((float)(_random.NextDouble() * 2 - 1)) * noise;
            samples[i] = x;
        }
    }

    /// <summary>
    /// Squelch closing: a short burst of receiver noise after the transmission ends, fading out;
    /// <paramref name="position"/> and <paramref name="total"/> place this chunk within the burst.
    /// </summary>
    public void SquelchTail(Span<float> samples, int position, int total)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float envelope = Math.Max(0, 1 - (float)(position + i) / total);
            samples[i] = Noise ? _noiseBand.Process((float)(_random.NextDouble() * 2 - 1)) * SquelchLevel * envelope : 0;
        }
    }
}
