namespace DcMeta.Core.Analysis;

public enum PlacementWeightMode
{
    /// <summary>Fixed weight by placement band (see <see cref="AnalysisSettings.PlacementTable"/>).</summary>
    Table,
    /// <summary>PW = (log2 F − log2 placement_mid + 1) / (log2 F + 1). 1st place = 1.0, scales with field.</summary>
    Log,
}

/// <summary>Every tunable choice the Master Prompt leaves open, in one place.</summary>
public sealed class AnalysisSettings
{
    // ---- Tournament coefficient
    public PlacementWeightMode WeightMode { get; set; } = PlacementWeightMode.Table;

    /// <summary>(max placement_mid, weight). First band whose bound ≥ placement_mid wins.</summary>
    public List<(double MaxMid, double Weight)> PlacementTable { get; set; } = new()
    {
        (1.0, 1.00),   // 1st
        (2.0, 0.85),   // 2nd
        (3.5, 0.70),   // 3-4
        (6.5, 0.55),   // 5-8
        (12.5, 0.40),  // 9-16
        (24.5, 0.30),  // 17-32
        (double.MaxValue, 0.20),
    };

    // ---- Deck filters
    public bool IncludeLeagues { get; set; } = false;  // MTGO league "rank" is a record, not a placement
    public int MinFieldSize { get; set; } = 2;
    public bool Strict99 { get; set; } = false;         // exclude decks whose mainboard ≠ 99

    // ---- Archetypes
    /// <summary>If set, decks with ≥ this many creatures are Creature-based; otherwise 2-means decides.</summary>
    public int? CreatureThreshold { get; set; }
    public int BorderlineBand { get; set; } = 2;

    // ---- Card value
    public double InclusionWeight { get; set; } = 0.5;
    public double DensityWeight { get; set; } = 0.5;
    /// <summary>Pseudo-count k for stability-adjusted Avg_C: (n·avg + k·mean) / (n + k).</summary>
    public double ShrinkageK { get; set; } = 5;

    // ---- Availability windows (new cards)
    /// <summary>
    /// Measure each card only against decks dated on/after its release (Scryfall date, else the first
    /// date it appears in the data), so a recently printed card isn't penalised for decks that predate it.
    /// </summary>
    public bool AvailabilityWindows { get; set; } = false;
    /// <summary>Pseudo-count (in decks) pulling a short window's density back toward its whole-dataset density.</summary>
    public double WindowShrinkK { get; set; } = 5;
    /// <summary>A card with a short window still needs at least this many decks to be recommended.</summary>
    public int WindowMinDecks { get; set; } = 3;

    // ---- Recency weighting
    /// <summary>
    /// If set, a deck's weight in Value halves every this many days, counted back from the newest deck.
    /// Avg_C is not affected. Null = all decks count equally.
    /// </summary>
    public double? RecencyHalfLifeDays { get; set; }

    // ---- Recommended 99
    public int StabilityFloor { get; set; } = 15;
    /// <summary>Weight of the Avg_C refinement in the selection score (Value keeps 1 − β).</summary>
    public double AvgCRefinement { get; set; } = 0.25;
    public int CutListMinDecks { get; set; } = 2;

    // ---- Cross-archetype
    public int CrossMinDecksPerSide { get; set; } = 3;
    /// <summary>|ΔAvg_C| needed to flag; null = 0.5 × SD of deck C.</summary>
    public double? CrossDeltaThreshold { get; set; }

    // ---- Advanced
    public int HighFieldThreshold { get; set; } = 128;

    /// <summary>The Avg_C difference the prompt asks to interpret.</summary>
    public double ReferenceDelta { get; set; } = 0.7;

    public static readonly HashSet<string> BasicLands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Island", "Mountain", "Plains", "Swamp", "Forest", "Wastes",
        "Snow-Covered Island", "Snow-Covered Mountain", "Snow-Covered Plains",
        "Snow-Covered Swamp", "Snow-Covered Forest",
    };
}
