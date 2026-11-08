namespace DcMeta.Core.Analysis;

public static class Archetypes
{
    public const string CreatureBased = "Creature-based";
    public const string CreatureLight = "Creature-light";
}

public sealed record AuditRow(Deck Deck, bool Included, string Reason);

public sealed class ScoredDeck
{
    public required Deck Deck { get; init; }
    public double LnField { get; init; }
    public double PlacementWeight { get; init; }
    public double C { get; init; }
    public string Archetype { get; set; } = "";
    public double CreatureDensity { get; set; }
    public double SpellDensity { get; set; }
    public double? InteractionDensity { get; set; }
    public double? VelocityDensity { get; set; }
    public double Silhouette { get; set; }
    public bool Borderline { get; set; }
}

public sealed record ArchetypeStats(
    string Archetype, int Decks, double SharePct, double AvgC, double SdC, double MedianC, double TotalC,
    double MeanCreatures, double SdCreatures, int MinCreatures, int MaxCreatures,
    double MeanSpellDensity, double? MeanInteractionDensity, double? MeanVelocityDensity,
    double MeanSilhouette, double BorderlinePct);

public sealed class CardMetric
{
    public required string Card { get; init; }
    public required string Section { get; init; }
    public int DecksPresent { get; init; }
    public double Popularity { get; init; }
    public double Inclusion { get; init; }
    public double Density { get; init; }
    public double InclusionNorm { get; set; }
    public double DensityNorm { get; set; }
    public double Value { get; set; }
    public double AvgC { get; init; }
    public double AvgCShrunk { get; set; }
    public double AvgCZ { get; set; }
    public double MedianQty { get; init; }
    public string TopExamples { get; init; } = "";
    public IReadOnlyList<double> CValues { get; init; } = Array.Empty<double>();
}

public sealed record RecommendedCard(
    string Card, string Category, int Qty, double Value, double AvgC, double AvgCShrunk,
    int DecksPresent, double Score, int FloorUsed, string Reason);

public sealed record CutCard(
    string Card, string Category, string Verdict, double Value, double AvgC, int DecksPresent,
    double Score, string Reason);

public sealed record CrossRow(
    string Card, string Section, int NCreatureBased, double? AvgCCreatureBased, int NCreatureLight,
    double? AvgCCreatureLight, double? Delta, double? WelchT, bool Significant, string Note);

public sealed record StructureRow(string Metric, double DatasetMedian, double DatasetMean, double Recommended);

public sealed class StatSummary
{
    public double MinAvgC, MaxAvgC, RangeAvgC, SdAvgC;
    public double MinAvgCStable, MaxAvgCStable, RangeAvgCStable, SdAvgCStable;
    public int StableCards;
    public double MeanDeckC, SdDeckC, MinDeckC, MaxDeckC;
    public double RefDelta, RefPctOfRange, RefPctOfRangeStable, RefInDeckSd;
    public double TypicalN, SeAtTypicalN, RefInSe;
    public double CorrValueInclusion, CorrValueAvgC, CorrPopularityAvgC;
}

public sealed class AnalysisResult
{
    public required AnalysisSettings Settings { get; init; }
    public required Dataset Data { get; init; }
    public List<AuditRow> Audit { get; } = new();
    public List<ScoredDeck> Decks { get; } = new();
    public double CreatureThresholdUsed { get; set; }
    public string ClusteringMethod { get; set; } = "";
    public bool HasRoles { get; set; }
    public List<ArchetypeStats> ArchetypeStats { get; } = new();
    public List<CardMetric> Cards { get; set; } = new();
    public List<CardMetric> CardsCreatureBased { get; set; } = new();
    public List<CardMetric> CardsCreatureLight { get; set; } = new();
    public List<CardMetric> CardsHighField { get; set; } = new();
    public int HighFieldDecks { get; set; }
    public List<RecommendedCard> Recommended { get; } = new();
    public Dictionary<string, int> SectionQuotas { get; } = new();
    public List<string> RecommendedNotes { get; } = new();
    public List<CutCard> CutList { get; } = new();
    public List<CrossRow> Cross { get; } = new();
    public double CrossThresholdUsed { get; set; }
    public List<StructureRow> Structure { get; } = new();
    public StatSummary Summary { get; } = new();
    public int EffectiveFloor { get; set; }
}
