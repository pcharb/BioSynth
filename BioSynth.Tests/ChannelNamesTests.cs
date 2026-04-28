using BioSynth;
using FluentAssertions;
using Xunit;

namespace BioSynth.Tests;

/// <summary>
/// Tests unitaires pour ChannelNames.GetChannelName.
/// Valide le mapping index → nom d'électrode 10-20.
/// </summary>
public class ChannelNamesTests
{
    // ── Noms standards ────────────────────────────────────────────────────

    [Fact]
    public void GetChannelName_8Channels_ReturnsCorrectNames()
    {
        var expected = new[] { "Fp1","Fp2","C3","C4","P3","P4","O1","O2" };

        for (int i = 0; i < 8; i++)
            ChannelNames.GetChannelName(i, 8).Should().Be(expected[i],
                because: $"index {i} avec 8 canaux doit être {expected[i]}");
    }

    [Fact]
    public void GetChannelName_16Channels_ContainsMidlineElectrodes()
    {
        var names = Enumerable.Range(0, 16)
                              .Select(i => ChannelNames.GetChannelName(i, 16))
                              .ToList();

        names.Should().Contain("Fz");
        names.Should().Contain("Cz");
        names.Should().Contain("Pz");
        names.Should().Contain("Oz");
    }

    [Fact]
    public void GetChannelName_32Channels_Returns32UniqueNames()
    {
        var names = Enumerable.Range(0, 32)
                              .Select(i => ChannelNames.GetChannelName(i, 32))
                              .ToList();

        names.Should().HaveCount(32);
        names.Distinct().Should().HaveCount(32, "chaque canal doit avoir un nom unique");
    }

    [Fact]
    public void GetChannelName_64Channels_Returns64UniqueNames()
    {
        var names = Enumerable.Range(0, 64)
                              .Select(i => ChannelNames.GetChannelName(i, 64))
                              .ToList();

        names.Should().HaveCount(64);
        names.Distinct().Should().HaveCount(64);
    }

    [Fact]
    public void GetChannelName_FirstChannel_IsAlwaysFp1()
    {
        foreach (int total in new[] { 8, 16, 32, 64 })
            ChannelNames.GetChannelName(0, total).Should().Be("Fp1",
                because: $"le premier canal parmi {total} doit être Fp1");
    }

    // ── Canaux hors liste ────────────────────────────────────────────────

    [Fact]
    public void GetChannelName_IndexBeyondKnownList_ReturnsFallback()
    {
        // Avec 64 canaux, l'index 65 est hors liste
        string name = ChannelNames.GetChannelName(65, 64);

        name.Should().StartWith("Ch", because: "le fallback doit être 'ChN'");
    }

    // ── Noms 10-20 standard ───────────────────────────────────────────────

    [Theory]
    [InlineData("Fp1")] [InlineData("Fp2")]
    [InlineData("C3")]  [InlineData("C4")]  [InlineData("Cz")]
    [InlineData("O1")]  [InlineData("O2")]  [InlineData("Oz")]
    public void GetChannelName_StandardElectrodes_PresentInAnyConfig(string electrode)
    {
        // Au moins une configuration sur 4 doit contenir cette électrode
        bool found = new[] { 8, 16, 32, 64 }.Any(total =>
            Enumerable.Range(0, total)
                      .Select(i => ChannelNames.GetChannelName(i, total))
                      .Contains(electrode));

        found.Should().BeTrue(because: $"{electrode} est une électrode standard 10-20");
    }
}
