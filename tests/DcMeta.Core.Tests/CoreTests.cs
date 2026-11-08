using DcMeta.Core;
using DcMeta.Core.Analysis;
using Xunit;

namespace DcMeta.Core.Tests;

public class CsvTests
{
    [Fact]
    public void RoundTrip_PreservesCommasQuotesAndNumbers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dcmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var d = TestData.Deck("1", 32, "3-4", ev: "Cup \"Winter\", Prague");
            d.NMainboard = 99;
            var cards = new List<CardEntry>
            {
                new("1", 1, "Jace, Vryn's Prodigy", Sections.Creature),
                new("1", 1, "Fire // Ice", Sections.InstantSorcery),
            };
            CsvIo.WriteDecks(Path.Combine(dir, "decks.csv"), new[] { d });
            CsvIo.WriteCards(Path.Combine(dir, "cards.csv"), cards);

            var back = CsvIo.Read(Path.Combine(dir, "decks.csv"), Path.Combine(dir, "cards.csv"));

            Assert.Equal("Cup \"Winter\", Prague", back.Decks[0].Event);
            Assert.Equal(32, back.Decks[0].FieldSize);
            Assert.Equal(3.5, back.Decks[0].PlacementMid);
            Assert.Equal("Jace, Vryn's Prodigy", back.Cards[0].Card);
            Assert.Equal("Fire // Ice", back.Cards[1].Card);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("1", 1, 1)]
    [InlineData("3-4", 3, 4)]
    [InlineData("5-8", 5, 8)]
    public void ParsePlacement_ReadsRanges(string raw, int best, int worst)
    {
        var (b, w) = Deck.ParsePlacement(raw);
        Assert.Equal(best, b);
        Assert.Equal(worst, w);
    }

    [Fact]
    public void Flags_MarkLeaguesAndWrongCounts()
    {
        var d = TestData.Deck("1", 16, "5", ev: "MTGO League");
        d.NMainboard = 98;
        d.RecomputeFlags();
        Assert.Contains("league_rank_is_record_not_placement", d.Flags);
        Assert.Contains("mainboard_count_98", d.Flags);
    }
}

public class UrlTests
{
    [Fact]
    public void ArchetypeUrl_KeepsAllFiltersAndReadsFormat()
    {
        var (url, format) = MtgTop8Url.Parse("https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop");
        Assert.Equal("https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop", url.ToString());
        Assert.Equal("EDH", format);
    }

    [Fact]
    public void ArchetypeUrl_UpgradesHttpAndDefaultsFormat()
    {
        var (url, format) = MtgTop8Url.Parse("http://mtgtop8.com/archetype?a=2749");
        Assert.Equal("https", url.Scheme);
        Assert.Equal("EDH", format);
    }

    [Theory]
    [InlineData("https://www.mtgtop8.com/event?e=1&d=2")]
    [InlineData("https://www.mtgtop8.com/archetype?meta=308")]
    [InlineData("https://notmtgtop8.com/archetype?a=1")]
    [InlineData("not a url")]
    public void NonArchetypeUrls_AreRejected(string bad)
    {
        bool threw = false;
        try { MtgTop8Url.Parse(bad); } catch (ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}

public class CoefficientTests
{
    [Fact]
    public void TableWeights_FollowBands()
    {
        var a = new MetaAnalyzer(new AnalysisSettings());
        Assert.Equal(1.00, a.PlacementWeight(1, 32));
        Assert.Equal(0.85, a.PlacementWeight(2, 32));
        Assert.Equal(0.70, a.PlacementWeight(3.5, 32));
        Assert.Equal(0.55, a.PlacementWeight(6.5, 32));
    }

    [Fact]
    public void LogWeights_FirstIsOneAndDecrease()
    {
        var a = new MetaAnalyzer(new AnalysisSettings { WeightMode = PlacementWeightMode.Log });
        Assert.Equal(1.0, a.PlacementWeight(1, 64), 6);
        Assert.True(a.PlacementWeight(2, 64) > a.PlacementWeight(3.5, 64));
        Assert.True(a.PlacementWeight(3.5, 64) > a.PlacementWeight(6.5, 64));
    }

    [Fact]
    public void C_IsLnFieldTimesWeight_AndLeaguesExcluded()
    {
        var (d1, c1) = TestData.FullDeck("1", 64, "1", 20);
        var (d2, c2) = TestData.FullDeck("2", 16, "3-4", 10);
        var (d3, c3) = TestData.FullDeck("3", 16, "5", 10);
        d3.Event = "MTGO League";
        var r = new MetaAnalyzer(new AnalysisSettings()).Run(TestData.Build((d1, c1), (d2, c2), (d3, c3)));

        Assert.Equal(2, r.Decks.Count);
        Assert.Equal(Math.Log(64) * 1.0, r.Decks.Single(d => d.Deck.DeckId == "1").C, 9);
        Assert.Equal(Math.Log(16) * 0.70, r.Decks.Single(d => d.Deck.DeckId == "2").C, 9);
        Assert.False(r.Audit.Single(a => a.Deck.DeckId == "3").Included);
    }
}

public class StatsTests
{
    [Fact]
    public void TwoMeans_SplitsBetweenClearGroups()
    {
        double t = Stats.OneDimTwoMeansThreshold(new double[] { 8, 9, 10, 11, 20, 21, 22, 23 });
        Assert.Equal(20, t);
    }

    [Fact]
    public void Median_HandlesEvenAndOdd()
    {
        Assert.Equal(2, Stats.Median(new double[] { 3, 1, 2 }));
        Assert.Equal(2.5, Stats.Median(new double[] { 4, 1, 2, 3 }));
    }
}

public class CardMetricTests
{
    static AnalysisResult Small()
    {
        // "Gem" only in the two winning decks; "Glue" everywhere.
        var decks = new[]
        {
            TestData.FullDeck("1", 64, "1", 20, ("Gem", Sections.Creature), ("Glue", Sections.InstantSorcery)),
            TestData.FullDeck("2", 32, "1", 20, ("Gem", Sections.Creature), ("Glue", Sections.InstantSorcery)),
            TestData.FullDeck("3", 32, "5-8", 10, ("Glue", Sections.InstantSorcery)),
            TestData.FullDeck("4", 16, "5-8", 10, ("Glue", Sections.InstantSorcery)),
        };
        return new MetaAnalyzer(new AnalysisSettings { StabilityFloor = 2 }).Run(TestData.Build(decks));
    }

    [Fact]
    public void Inclusion_AvgC_AndPopularity_MatchDefinitions()
    {
        var r = Small();
        var c = r.Decks.ToDictionary(d => d.Deck.DeckId, d => d.C);
        var gem = r.Cards.Single(x => x.Card == "Gem");
        var glue = r.Cards.Single(x => x.Card == "Glue");

        Assert.Equal(c["1"] + c["2"], gem.Inclusion, 9);
        Assert.Equal(2, gem.DecksPresent);
        Assert.Equal(0.5, gem.Popularity, 9);
        Assert.Equal((c["1"] + c["2"]) / 2, gem.AvgC, 9);
        Assert.Equal(c.Values.Average(), glue.AvgC, 9);   // ubiquitous card → dataset mean
        Assert.True(gem.AvgC > glue.AvgC);
        Assert.True(glue.Value > gem.Value);              // centrality vs concentration
    }

    [Fact]
    public void DensityNorm_EqualsInclusionNorm_UnderPromptDefinitions()
    {
        foreach (var m in Small().Cards)
            Assert.Equal(m.InclusionNorm, m.DensityNorm, 9);
    }

    [Fact]
    public void Shrinkage_PullsSmallSamplesTowardMean()
    {
        var r = Small();
        var gem = r.Cards.Single(x => x.Card == "Gem");
        double mean = r.Decks.Average(d => d.C);
        Assert.True(gem.AvgCShrunk < gem.AvgC);
        Assert.True(gem.AvgCShrunk > mean);
    }

    [Fact]
    public void Archetypes_SplitByCreatureCount()
    {
        var r = Small();
        Assert.All(r.Decks.Where(d => d.Deck.NCreatures > 15), d => Assert.Equal(Archetypes.CreatureBased, d.Archetype));
        Assert.All(r.Decks.Where(d => d.Deck.NCreatures < 15), d => Assert.Equal(Archetypes.CreatureLight, d.Archetype));
    }
}

public class AvailabilityWindowTests
{
    /// <summary>
    /// 20 equal decks on 1–20 Jan 2026. "New Bomb" is in all of the last 5 (released 16 Jan);
    /// "Old Half" is in the first 10 (old card that fell out of favour).
    /// </summary>
    static Dataset Data()
    {
        var decks = Enumerable.Range(1, 20).Select(i =>
        {
            var extra = new List<(string, string)>();
            if (i >= 16) extra.Add(("New Bomb", Sections.InstantSorcery));
            if (i <= 10) extra.Add(("Old Half", Sections.InstantSorcery));
            var (d, cards) = TestData.FullDeck(i.ToString(), 32, "1", 15, extra.ToArray());
            d.Date = $"{i:00}/01/26";
            return (d, cards);
        }).ToArray();
        return TestData.Build(decks);
    }

    static AnalysisResult Run(bool windows, Dictionary<string, DateOnly>? release = null) =>
        new MetaAnalyzer(new AnalysisSettings { StabilityFloor = 10, AvailabilityWindows = windows }, releaseDates: release).Run(Data());

    [Fact]
    public void DeckDate_ParsesMtgTop8Format()
    {
        Assert.Equal(new DateOnly(2026, 11, 3), new Deck { Date = "03/11/26" }.ParsedDate);
        Assert.Null(new Deck { Date = "" }.ParsedDate);
    }

    [Fact]
    public void WithoutWindows_NewCardIsPenalised()
    {
        var bomb = Run(false).Cards.Single(c => c.Card == "New Bomb");
        Assert.Equal(0.25, bomb.Popularity, 9);
        Assert.False(bomb.IsWindowed);
        Assert.Equal(20, bomb.WindowDecks);
    }

    [Fact]
    public void ReleaseDate_SetsWindow_AndRaisesPopularityAndValue()
    {
        var off = Run(false).Cards.Single(c => c.Card == "New Bomb");
        var r = Run(true, new() { ["New Bomb"] = new DateOnly(2026, 1, 16) });
        var bomb = r.Cards.Single(c => c.Card == "New Bomb");

        Assert.True(bomb.IsWindowed);
        Assert.Equal(5, bomb.WindowDecks);
        Assert.Equal("Scryfall", bomb.AvailableSource);
        Assert.Equal(1.0, bomb.Popularity, 9);
        // Equal C per deck: (5 + k·0.25) / (5 + k) with k = 5 → 0.625, between raw 0.25 and window 1.0.
        Assert.Equal(0.625, bomb.Density, 9);
        Assert.True(bomb.Value > off.Value);
        Assert.Equal(3, r.CardFloor(bomb, 10));             // ceil(10 × 5/20) = 3
        Assert.True(bomb.DecksPresent >= r.CardFloor(bomb, 10));
    }

    [Fact]
    public void WithoutReleaseDate_FallsBackToFirstSeen()
    {
        var bomb = Run(true).Cards.Single(c => c.Card == "New Bomb");
        Assert.Equal("first seen", bomb.AvailableSource);
        Assert.Equal(new DateOnly(2026, 1, 16), bomb.AvailableFrom);
        Assert.Equal(5, bomb.WindowDecks);
    }

    [Fact]
    public void OldCards_AreUnchangedByWindows()
    {
        var off = Run(false).Cards.ToDictionary(c => c.Card);
        var on = Run(true).Cards.ToDictionary(c => c.Card);
        foreach (var name in new[] { "Old Half", "Island", "Spell 0", "Creature 0" })
        {
            Assert.False(on[name].IsWindowed);
            Assert.Equal(off[name].Popularity, on[name].Popularity, 9);
            Assert.Equal(off[name].Density, on[name].Density, 9);
        }
    }

    static AnalysisResult RunRecency(double? halfLife) =>
        new MetaAnalyzer(new AnalysisSettings { StabilityFloor = 10, RecencyHalfLifeDays = halfLife }).Run(Data());

    [Fact]
    public void Recency_HalvesWeightPerHalfLife()
    {
        var r = RunRecency(10);
        Assert.Equal(1.0, r.Decks.Single(d => d.Deck.DeckId == "20").Recency, 9);
        Assert.Equal(0.5, r.Decks.Single(d => d.Deck.DeckId == "10").Recency, 9);   // 10 days before the newest
        Assert.All(r.Decks, d => Assert.Equal(d.C * d.Recency, d.W, 9));
    }

    [Fact]
    public void Recency_ShiftsValueTowardRecentCards_ButNotAvgC()
    {
        var off = RunRecency(null).Cards.ToDictionary(c => c.Card);
        var on = RunRecency(10).Cards.ToDictionary(c => c.Card);

        Assert.True(on["New Bomb"].Value > off["New Bomb"].Value);   // played in the newest decks
        Assert.True(on["Old Half"].Value < off["Old Half"].Value);   // played only in the oldest decks
        Assert.Equal(off["New Bomb"].AvgC, on["New Bomb"].AvgC, 9);
        Assert.Equal(off["Old Half"].AvgC, on["Old Half"].AvgC, 9);
    }

    [Fact]
    public void ReleaseDateAfterAPlayingDeck_StillCountsThatDeck()
    {
        // A wrong (too late) release date must not drop decks that actually play the card.
        var bomb = Run(true, new() { ["New Bomb"] = new DateOnly(2026, 1, 19) }).Cards.Single(c => c.Card == "New Bomb");
        Assert.Equal(5, bomb.WindowDecks);
        Assert.Equal(1.0, bomb.Popularity, 9);
    }
}

public class RecommendedTests
{
    [Fact]
    public void Recommended_HasExactly99Cards_AndMatchesSectionQuotas()
    {
        var decks = Enumerable.Range(1, 20)
            .Select(i => TestData.FullDeck(i.ToString(), 32, i % 4 == 0 ? "1" : "5-8", i % 2 == 0 ? 20 : 12))
            .ToArray();
        var r = new MetaAnalyzer(new AnalysisSettings { StabilityFloor = 5 }).Run(TestData.Build(decks));

        Assert.Equal(99, r.Recommended.Sum(x => x.Qty));
        foreach (var (section, quota) in r.SectionQuotas)
            Assert.Equal(quota, r.Recommended.Where(x => x.Category == section).Sum(x => x.Qty));
        Assert.Equal(3, r.Recommended.Single(x => x.Card == "Island").Qty); // basics keep median copies
        Assert.Equal(r.Recommended.Count, r.Recommended.Select(x => x.Card).Distinct().Count());
    }

    [Fact]
    public void Floor_IsLoweredWhenLargerThanHalfTheDataset()
    {
        var decks = Enumerable.Range(1, 6).Select(i => TestData.FullDeck(i.ToString(), 16, "1", 15)).ToArray();
        var r = new MetaAnalyzer(new AnalysisSettings { StabilityFloor = 15 }).Run(TestData.Build(decks));
        Assert.Equal(2, r.EffectiveFloor); // ceil(0.3 × 6)
    }
}
