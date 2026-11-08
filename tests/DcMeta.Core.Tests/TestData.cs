using DcMeta.Core;

namespace DcMeta.Core.Tests;

/// <summary>Builds small hand-checkable datasets.</summary>
internal static class TestData
{
    public static Deck Deck(string id, int field, string rank, string ev = "Event")
    {
        var (b, w) = Core.Deck.ParsePlacement(rank);
        return new Deck
        {
            DeckId = id, EventId = "e" + id, Player = "P" + id, Event = ev, RankRaw = rank, FieldSize = field,
            PlacementBest = b, PlacementWorst = w, PlacementMid = b is null ? null : (b + w) / 2.0,
        };
    }

    /// <summary>
    /// A full 99-card deck: <paramref name="creatures"/> creatures, 30 lands (3 Island + 27 named),
    /// rest instants/sorceries, plus any <paramref name="extra"/> cards replacing spells.
    /// </summary>
    public static (Deck Deck, List<CardEntry> Cards) FullDeck(string id, int field, string rank, int creatures,
                                                              params (string Card, string Section)[] extra)
    {
        var d = Deck(id, field, rank);
        var cards = new List<CardEntry> { new(id, 3, "Island", Sections.Land) };
        cards.AddRange(Enumerable.Range(0, 27).Select(i => new CardEntry(id, 1, $"Land {i}", Sections.Land)));
        cards.AddRange(Enumerable.Range(0, creatures).Select(i => new CardEntry(id, 1, $"Creature {i}", Sections.Creature)));
        cards.AddRange(extra.Select(e => new CardEntry(id, 1, e.Card, e.Section)));
        int spells = 99 - cards.Sum(c => c.Qty);
        cards.AddRange(Enumerable.Range(0, spells).Select(i => new CardEntry(id, 1, $"Spell {i}", Sections.InstantSorcery)));
        d.NMainboard = cards.Sum(c => c.Qty);
        d.NCreatures = cards.Where(c => c.Section == Sections.Creature).Sum(c => c.Qty);
        d.NLands = 30;
        d.NInstantSorcery = cards.Where(c => c.Section == Sections.InstantSorcery).Sum(c => c.Qty);
        return (d, cards);
    }

    public static Dataset Build(params (Deck Deck, List<CardEntry> Cards)[] decks) =>
        new() { Decks = decks.Select(x => x.Deck).ToList(), Cards = decks.SelectMany(x => x.Cards).ToList() };
}
