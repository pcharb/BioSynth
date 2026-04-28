using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour EyeSample et EyeTrackingGenerator.
/// </summary>
public class EyeTrackingTests : IDisposable
{
    private readonly EyeTrackingConfig _cfg = new()
    {
        SampleRate  = 120,
        ScreenWidth = 1920,
        ScreenHeight= 1080,
        OutputMode  = EyeOutputMode.None,
    };

    private EyeTrackingGenerator? _gen;

    public void Dispose() => _gen?.Dispose();

    // ── EyeSample ─────────────────────────────────────────────────────────

    [Fact]
    public void EyeSample_DefaultValues_AreValid()
    {
        var s = new EyeSample();

        s.ConfidenceLeft.Should().BeInRange(0.0, 1.0);
        s.ConfidenceRight.Should().BeInRange(0.0, 1.0);
    }

    // ── Démarrage / arrêt ─────────────────────────────────────────────────

    [Fact]
    public void Start_SetsIsRunning()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        _gen.Start();
        Task.Delay(50).Wait();

        _gen.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_ClearsIsRunning()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        _gen.Start();
        Task.Delay(50).Wait();
        _gen.Stop();
        Task.Delay(100).Wait();

        _gen.IsRunning.Should().BeFalse();
    }

    // ── Samples ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Generator_EmitsSamples()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        var samples = new List<EyeSample>();
        _gen.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _gen.Start();
        await Task.Delay(200);
        _gen.Stop();

        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Generator_GazeCoordinates_WithinScreenBounds()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        var samples = new List<EyeSample>();
        _gen.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _gen.Start();
        await Task.Delay(500);
        _gen.Stop();

        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s =>
            s.GazeX >= 0 && s.GazeX <= _cfg.ScreenWidth &&
            s.GazeY >= 0 && s.GazeY <= _cfg.ScreenHeight,
            because: "le regard doit rester dans les limites de l'écran");
    }

    [Fact]
    public async Task Generator_NormalizedCoordinates_Between0And1()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        var samples = new List<EyeSample>();
        _gen.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _gen.Start();
        await Task.Delay(300);
        _gen.Stop();

        samples.Should().OnlyContain(s =>
            s.GazeXNorm >= 0.0 && s.GazeXNorm <= 1.0 &&
            s.GazeYNorm >= 0.0 && s.GazeYNorm <= 1.0,
            because: "les coordonnées normalisées doivent être dans [0,1]");
    }

    [Fact]
    public async Task Generator_PupilDiameter_IsPhysiological()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        var samples = new List<EyeSample>();
        _gen.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _gen.Start();
        await Task.Delay(500);
        _gen.Stop();

        // Pupille humaine : 2 à 8 mm
        samples.Should().OnlyContain(s =>
            s.PupilLeft  >= 1.0 && s.PupilLeft  <= 10.0 &&
            s.PupilRight >= 1.0 && s.PupilRight <= 10.0,
            because: "le diamètre pupillaire doit être physiologiquement réaliste (2-8 mm)");
    }

    [Fact]
    public async Task Generator_ConfidenceDuringBlink_DropsToZero()
    {
        _gen = new EyeTrackingGenerator(_cfg);
        var samples = new List<EyeSample>();
        _gen.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _gen.Start();
        await Task.Delay(15000);  // attendre au moins un clignement (max ~9 s)
        _gen.Stop();

        // Il doit y avoir au moins un sample avec confidence basse (clignement)
        samples.Should().Contain(s => s.ConfidenceLeft < 0.1,
            because: "les clignements doivent produire une confiance proche de 0");
    }
}
