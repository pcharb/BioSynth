using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour EEGSample et EEGConfig.
/// </summary>
public class EEGSampleTests
{
    // ── EEGSample ────────────────────────────────────────────────────────

    [Fact]
    public void EEGSample_Constructor_CreatesCorrectChannelArray()
    {
        var sample = new EEGSample(8);

        sample.Channels.Should().HaveCount(8);
        sample.Channels.Should().OnlyContain(v => v == 0.0);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void EEGSample_SupportsAllStandardChannelCounts(int channels)
    {
        var sample = new EEGSample(channels);

        sample.Channels.Should().HaveCount(channels);
    }

    [Fact]
    public void EEGSample_CanStoreAndRetrieveValues()
    {
        var sample = new EEGSample(4);
        sample.Channels[0] = 12.5;
        sample.Channels[1] = -75.3;
        sample.Channels[2] = 200.0;
        sample.Channels[3] = 0.001;

        sample.Channels[0].Should().BeApproximately(12.5,   0.001);
        sample.Channels[1].Should().BeApproximately(-75.3,  0.001);
        sample.Channels[2].Should().BeApproximately(200.0,  0.001);
        sample.Channels[3].Should().BeApproximately(0.001,  0.0001);
    }

    [Fact]
    public void EEGSample_TimestampDefaultsToZero()
    {
        var sample = new EEGSample(8);

        sample.Timestamp.Should().Be(0);
    }

    [Fact]
    public void EEGSample_TimestampCanBeSet()
    {
        var sample = new EEGSample(8) { Timestamp = 1_234_567_890L };

        sample.Timestamp.Should().Be(1_234_567_890L);
    }

    // ── EEGConfig ────────────────────────────────────────────────────────

    [Fact]
    public void EEGConfig_DefaultValues_ArePhysiologicallyReasonable()
    {
        var cfg = new EEGConfig();

        cfg.ChannelCount.Should().Be(8);
        cfg.SampleRate.Should().Be(256);
        cfg.NoiseLevel.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void EEGConfig_NoiseLevelClampedBetweenZeroAndOne()
    {
        var cfg = new EEGConfig { NoiseLevel = 0.5 };

        cfg.NoiseLevel.Should().BeInRange(0.0, 1.0);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void EEGConfig_AcceptsStandardChannelCounts(int n)
    {
        var cfg = new EEGConfig { ChannelCount = n };
        cfg.ChannelCount.Should().Be(n);
    }
}
