namespace DcMeta.Core;

/// <summary>Normalized card type, taken from MTGTop8's own deck sections.</summary>
public static class Sections
{
    public const string Land = "Land";
    public const string Creature = "Creature";
    public const string InstantSorcery = "Instant/Sorcery";
    public const string Other = "Other";          // artifacts, enchantments, planeswalkers
    public const string Commander = "Commander";
    public const string Unknown = "Unknown";

    public static readonly string[] Mainboard = { Creature, InstantSorcery, Other, Land, Unknown };
}

public sealed record CardEntry(string DeckId, int Qty, string Card, string Section);

/// <summary>One scraped deck: tournament metadata + type counts. Mirrors decks.csv.</summary>
public sealed class Deck
{
    public string DeckId { get; set; } = "";
    public string EventId { get; set; } = "";
    public string DeckName { get; set; } = "";
    public string Player { get; set; } = "";
    public string Event { get; set; } = "";
    public string Level { get; set; } = "";
    public string RankRaw { get; set; } = "";
    public string Date { get; set; } = "";
    public int? FieldSize { get; set; }
    public string Record { get; set; } = "";
    public int? PlacementBest { get; set; }
    public int? PlacementWorst { get; set; }
    public double? PlacementMid { get; set; }
    public string Commander { get; set; } = "";
    public int NMainboard { get; set; }
    public int NLands { get; set; }
    public int NCreatures { get; set; }
    public int NInstantSorcery { get; set; }
    public int NOtherSpells { get; set; }
    public int NUnknownType { get; set; }
    public string DeckUrl { get; set; } = "";
    public string Flags { get; set; } = "";

    public string Label => $"{Player} @ {Event} ({RankRaw})";

    public bool IsLeague => Event.Contains("league", StringComparison.OrdinalIgnoreCase);

    public void RecomputeFlags()
    {
        var f = new List<string>();
        if (FieldSize is null) f.Add("missing_field_size");
        if (PlacementBest is null) f.Add("missing_placement");
        if (IsLeague) f.Add("league_rank_is_record_not_placement"); // MTGO leagues: "5" = 5-0
        if (NMainboard != 99) f.Add($"mainboard_count_{NMainboard}");
        if (NUnknownType > 0) f.Add("no_type_sections");
        Flags = string.Join(";", f);
    }

    /// <summary>'1' -> (1,1); '3-4' -> (3,4); '5-8' -> (5,8).</summary>
    public static (int? Best, int? Worst) ParsePlacement(string rankRaw)
    {
        var nums = System.Text.RegularExpressions.Regex.Matches(rankRaw ?? "", @"\d+")
            .Select(m => int.Parse(m.Value)).ToList();
        return nums.Count == 0 ? (null, null) : (nums[0], nums[^1]);
    }
}

/// <summary>Full parsed dataset: decks + one row per (deck, card).</summary>
public sealed class Dataset
{
    public List<Deck> Decks { get; init; } = new();
    public List<CardEntry> Cards { get; init; } = new();
}
