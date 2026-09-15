using Tf.Core.Brain;

namespace Tf.Core.Tests;

[Trait("Category", "Unit")]
public class EnsemblePlanParserTests
{
    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(EnsemblePlanParser.Parse(null));
        Assert.Empty(EnsemblePlanParser.Parse(""));
        Assert.Empty(EnsemblePlanParser.Parse("   "));
    }

    [Fact]
    public void Parse_SingleBrain_DefaultWeight()
    {
        var entries = EnsemblePlanParser.Parse("Growth");

        var entry = Assert.Single(entries);
        Assert.Equal(("Growth", 1.0), entry);
    }

    [Fact]
    public void Parse_MixedSeparators_AndWeights()
    {
        var entries = EnsemblePlanParser.Parse("Growth:1.5, TrendFollowing; Breakout:0.5x");

        Assert.Equal(3, entries.Count);
        Assert.Equal(("Growth", 1.5), entries[0]);
        Assert.Equal(("TrendFollowing", 1.0), entries[1]);
        Assert.Equal(("Breakout", 0.5), entries[2]);
    }

    [Fact]
    public void Parse_UnknownBrain_Skipped()
    {
        var entries = EnsemblePlanParser.Parse("Growth LlmMagic TrendFollowing");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Growth", entries[0].Key);
        Assert.Equal("TrendFollowing", entries[1].Key);
    }

    [Fact]
    public void Parse_BadWeight_SkipsEntry()
    {
        // Zero/negative/non-numeric weights cannot vote — drop the entry.
        Assert.Empty(EnsemblePlanParser.Parse("Growth:0"));
        Assert.Empty(EnsemblePlanParser.Parse("Growth:-1"));
        Assert.Empty(EnsemblePlanParser.Parse("Growth:abc"));
    }

    [Fact]
    public void Parse_DuplicateKeys_CollapseToFirst()
    {
        var entries = EnsemblePlanParser.Parse("Growth:2.0 growth:0.5");

        var entry = Assert.Single(entries);
        Assert.Equal(("Growth", 2.0), entry);
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnKeys()
    {
        var entries = EnsemblePlanParser.Parse("growth:1.2 TRENDfollowing");

        Assert.Equal(2, entries.Count);
        Assert.Equal("growth", entries[0].Key);
        Assert.Equal("TRENDfollowing", entries[1].Key);
    }

    [Fact]
    public void Render_RoundTrips()
    {
        var entries = new List<(string, double)>
        {
            ("Growth", 1.5), ("TrendFollowing", 1.0), ("Breakout", 0.5)
        };

        var text = EnsemblePlanParser.Render(entries);
        var parsed = EnsemblePlanParser.Parse(text);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(("Growth", 1.5), parsed[0]);
        Assert.Equal(("TrendFollowing", 1.0), parsed[1]);
        Assert.Equal(("Breakout", 0.5), parsed[2]);
    }

    [Fact]
    public void Render_OmitsDefaultWeights()
    {
        var text = EnsemblePlanParser.Render([("Growth", 1.0), ("Breakout", 0.5)]);

        Assert.Equal("Growth Breakout:0.5", text);
    }
}
