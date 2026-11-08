using System.Text;
using DcMeta.Core;

namespace DcMeta.Scraper;

/// <summary>Scrapes one MTGTop8 archetype URL into decks.csv, cards_long.csv and dataset.txt.</summary>
public static class ScrapeJob
{
    /// <summary>
    /// <paramref name="url"/> is any MTGTop8 archetype page URL as copied from the browser
    /// (see <see cref="MtgTop8Url.Parse"/>); its filters apply to every listing page.
    /// </summary>
    public static void Run(string url, string outDir, int pages = 50, int minPlayers = 0, double delaySeconds = 1.5)
    {
        var (uri, format) = MtgTop8Url.Parse(url);
        Directory.CreateDirectory(outDir);

        using var client = new MtgTop8Client(TimeSpan.FromSeconds(delaySeconds), format);
        var listings = client.ListDecks(uri.ToString(), pages).GetAwaiter().GetResult();

        var decks = new List<Deck>();
        var cards = new List<CardEntry>();
        var fieldCache = new Dictionary<string, int?>();

        for (int i = 0; i < listings.Count; i++)
        {
            var l = listings[i];
            Console.WriteLine($"[{i + 1}/{listings.Count}] {l.Player} - {l.Event}");
            int? fieldSize; string record; List<(int Qty, string Card, string Section)> list;
            try
            {
                (fieldSize, record, list) = client.GetDeck(l.EventId, l.DeckId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Term.Error($"  failed: {e.Message}");
                continue;
            }
            if (fieldSize is null && fieldCache.TryGetValue(l.EventId, out var cached)) fieldSize = cached;
            fieldCache[l.EventId] = fieldSize;

            var main = list.Where(c => c.Section != Sections.Commander).ToList();
            int T(string s) => main.Where(c => c.Section == s).Sum(c => c.Qty);
            var (best, worst) = Deck.ParsePlacement(l.RankRaw);

            var deck = new Deck
            {
                DeckId = l.DeckId, EventId = l.EventId, DeckName = l.DeckName, Player = l.Player, Event = l.Event,
                Level = l.Level, RankRaw = l.RankRaw, Date = l.Date, FieldSize = fieldSize, Record = record,
                PlacementBest = best, PlacementWorst = worst, PlacementMid = best is null ? null : (best + worst) / 2.0,
                Commander = string.Join(" / ", list.Where(c => c.Section == Sections.Commander).Select(c => c.Card)),
                NMainboard = main.Sum(c => c.Qty), NLands = T(Sections.Land), NCreatures = T(Sections.Creature),
                NInstantSorcery = T(Sections.InstantSorcery), NOtherSpells = T(Sections.Other), NUnknownType = T(Sections.Unknown),
                DeckUrl = $"{MtgTop8Client.Base}event?e={l.EventId}&d={l.DeckId}&f={format}",
            };
            deck.RecomputeFlags();
            decks.Add(deck);
            cards.AddRange(main.Select(c => new CardEntry(l.DeckId, c.Qty, c.Card, c.Section)));
        }

        if (minPlayers > 0) decks = decks.Where(d => (d.FieldSize ?? 0) >= minPlayers).ToList();
        var keep = decks.Select(d => d.DeckId).ToHashSet();
        cards = cards.Where(c => keep.Contains(c.DeckId)).ToList();

        CsvIo.WriteDecks(Path.Combine(outDir, "decks.csv"), decks);
        CsvIo.WriteCards(Path.Combine(outDir, "cards_long.csv"), cards);
        WriteDatasetText(Path.Combine(outDir, "dataset.txt"), decks, cards);

        Term.Ok($"\nSaved {decks.Count} decks, {cards.Select(c => c.Card).Distinct().Count()} unique cards to {Path.GetFullPath(outDir)}");
        Term.Info("Flag summary:");
        foreach (var g in decks.GroupBy(d => d.Flags == "" ? "ok" : d.Flags).OrderByDescending(g => g.Count()))
            (g.Key == "ok" ? (Action<string>)Term.Ok : Term.Warn)($"  {g.Count(),4}  {g.Key}");
    }

    /// <summary>LLM-friendly block: one header per deck, cards grouped by type.</summary>
    static void WriteDatasetText(string path, List<Deck> decks, List<CardEntry> cards)
    {
        var byDeck = cards.ToLookup(c => c.DeckId);
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine($"DATASET: {decks.Count} decklists (source: MTGTop8)");
        w.WriteLine("Placement given as range (e.g. 3-4 = lost in semifinal). Card types are MTGTop8's own section grouping.");
        w.WriteLine();
        foreach (var d in decks)
        {
            w.WriteLine($"### DECK {d.DeckId}");
            w.WriteLine($"Event: {d.Event} | Date: {d.Date} | Players: {d.FieldSize?.ToString() ?? "n/a"} | " +
                        $"Placement: {d.RankRaw} | Record: {(d.Record == "" ? "n/a" : d.Record)} | " +
                        $"Player: {d.Player} | Flags: {(d.Flags == "" ? "none" : d.Flags)}");
            w.WriteLine($"Counts: creatures={d.NCreatures} instants_sorceries={d.NInstantSorcery} " +
                        $"other={d.NOtherSpells} lands={d.NLands} total={d.NMainboard}");
            foreach (var sec in Sections.Mainboard)
            {
                var part = byDeck[d.DeckId].Where(c => c.Section == sec).ToList();
                if (part.Count > 0) w.WriteLine($"[{sec}] " + string.Join("; ", part.Select(c => $"{c.Qty} {c.Card}")));
            }
            w.WriteLine();
        }
    }
}
