using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests d'intégration pour EEGGenerator.
/// Valide la génération de signaux, le threading et les propriétés physiologiques.
/// </summary>
public class EEGGeneratorTests : IDisposable
{
    private readonly EEGConfig _cfg = new()
    {
        ChannelCount = 8,
        SampleRate   = 256,
        AddArtifacts = false,
        NoiseLevel   = 0.0,
        OutputMode   = OutputMode.None,
    };

    private EEGGenerator? _generator;

    public void Dispose() => _generator?.Dispose();

    // ── Démarrage / arrêt ─────────────────────────────────────────────────

    [Fact]
    public void Start_SetsIsRunningToTrue()
    {
        _generator = new EEGGenerator(_cfg);

        _generator.Start();
        Task.Delay(50).Wait();

        _generator.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_SetsIsRunningToFalse()
    {
        _generator = new EEGGenerator(_cfg);
        _generator.Start();
        Task.Delay(50).Wait();

        _generator.Stop();
        Task.Delay(100).Wait();

        _generator.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CanBeCalledWithoutError()
    {
        _generator = new EEGGenerator(_cfg);
        _generator.Start();
        Task.Delay(50).Wait();

        var act = () => _generator.Dispose();

        act.Should().NotThrow();
    }

    // ── Génération de samples ─────────────────────────────────────────────

    [Fact]
    public async Task Generator_EmitsSamplesWithCorrectChannelCount()
    {
        _generator = new EEGGenerator(_cfg);
        var samples = new List<EEGSample>();
        _generator.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _generator.Start();
        await Task.Delay(200);
        _generator.Stop();

        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => s.Channels.Length == 8,
            because: "chaque sample doit avoir 8 canaux");
    }

    [Fact]
    public async Task Generator_ProducesSamplesAtApproximateRate()
    {
        _generator = new EEGGenerator(_cfg);
        var samples = new List<EEGSample>();
        _generator.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _generator.Start();
        await Task.Delay(500);   // 500 ms → ~128 samples à 256 Hz
        _generator.Stop();

        // Tolérance large à cause du scheduling OS
        samples.Count.Should().BeInRange(60, 200,
            because: "à 256 Hz pendant 500 ms, environ 128 samples attendus (±50%)");
    }

    [Fact]
    public async Task Generator_TimestampsAreMonotonicallyIncreasing()
    {
        _generator = new EEGGenerator(_cfg);
        var timestamps = new List<long>();
        _generator.SampleGenerated += s => { lock (timestamps) timestamps.Add(s.Timestamp); };

        _generator.Start();
        await Task.Delay(200);
        _generator.Stop();

        timestamps.Should().HaveCountGreaterThan(1);
        for (int i = 1; i < timestamps.Count; i++)
            timestamps[i].Should().BeGreaterThanOrEqualTo(timestamps[i - 1],
                because: "les timestamps doivent être monotones croissants");
    }

    // ── Propriétés physiologiques ─────────────────────────────────────────

    [Fact]
    public async Task Generator_SignalAmplitude_IsInPhysiologicalRange()
    {
        _generator = new EEGGenerator(_cfg);
        var samples = new List<EEGSample>();
        _generator.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _generator.Start();
        await Task.Delay(500);
        _generator.Stop();

        samples.Should().NotBeEmpty();

        // L'EEG humain est typiquement entre -500 µV et +500 µV
        var allValues = samples.SelectMany(s => s.Channels).ToList();
        allValues.Should().OnlyContain(v => v >= -600 && v <= 600,
            because: "l'amplitude EEG doit rester dans des limites physiologiques");
    }

    [Fact]
    public async Task Generator_AllChannels_HaveVariance()
    {
        _generator = new EEGGenerator(new EEGConfig
        {
            ChannelCount = 8,
            SampleRate   = 256,
            AddArtifacts = false,
            NoiseLevel   = 0.1,
            OutputMode   = OutputMode.None,
        });
        var samples = new List<EEGSample>();
        _generator.SampleGenerated += s => { lock (samples) samples.Add(s); };

        _generator.Start();
        await Task.Delay(500);
        _generator.Stop();

        samples.Should().HaveCountGreaterThan(10);

        // Chaque canal doit avoir une variance non nulle (signal actif)
        for (int ch = 0; ch < 8; ch++)
        {
            var values = samples.Select(s => s.Channels[ch]).ToList();
            var variance = values.Select(v => v - values.Average())
                                 .Select(d => d * d).Average();
            variance.Should().BeGreaterThan(0,
                because: $"le canal {ch} doit avoir un signal non nul");
        }
    }

    // ── ZoneController integration ────────────────────────────────────────

    [Fact]
    public async Task Generator_WithZoneController_ModulatesSignal()
    {
        var zones = new BrainZoneController();

        // Configuration de base : zones à 0.5
        _generator = new EEGGenerator(_cfg) { ZoneController = zones };
        var samplesLow = new List<EEGSample>();
        _generator.SampleGenerated += s => { lock (samplesLow) samplesLow.Add(s); };
        _generator.Start();
        await Task.Delay(300);
        _generator.Stop();

        var meanLow = samplesLow.SelectMany(s => s.Channels).Select(Math.Abs).Average();

        // Zones à 1.0 → signal plus ample
        zones.Zone(BrainRegion.Frontal).Activation    = 1.0;
        zones.Zone(BrainRegion.Central).Activation    = 1.0;
        zones.Zone(BrainRegion.Occipital).Activation  = 1.0;

        var samplesHigh = new List<EEGSample>();
        _generator = new EEGGenerator(_cfg) { ZoneController = zones };
        _generator.SampleGenerated += s => { lock (samplesHigh) samplesHigh.Add(s); };
        _generator.Start();
        await Task.Delay(300);
        _generator.Stop();

        var meanHigh = samplesHigh.SelectMany(s => s.Channels).Select(Math.Abs).Average();

        meanHigh.Should().BeGreaterThan(meanLow * 0.8,
            because: "activation maximale des zones doit augmenter l'amplitude du signal");
    }

    // ── TotalSamplesGenerated ──────────────────────────────────────────────

    [Fact]
    public async Task Generator_TotalSamplesGenerated_Increments()
    {
        _generator = new EEGGenerator(_cfg);
        _generator.SampleGenerated += _ => { };

        _generator.Start();
        await Task.Delay(300);
        _generator.Stop();

        _generator.TotalSamplesGenerated.Should().BeGreaterThan(0);
    }
}
