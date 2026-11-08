using System.Globalization;
using System.Text;

namespace DcMeta.Core;

/// <summary>Reads/writes decks.csv and cards_long.csv (RFC 4180, UTF-8 with BOM, invariant culture).</summary>
public static class CsvIo
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] DeckHeader =
    {
        "deck_id", "event_id", "deck_name", "player", "event", "level", "rank_raw", "date",
        "field_size", "record", "placement_best", "placement_worst", "placement_mid", "commander",
        "n_mainboard", "n_lands", "n_creatures", "n_instant_sorcery", "n_other_spells",
        "n_unknown_type", "deck_url", "flags",
    };

    public static readonly string[] CardHeader = { "deck_id", "qty", "card", "section" };

    public static string Escape(object? v)
    {
        string s = v switch
        {
            null => "",
            double d => d.ToString("R", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static void WriteDecks(string path, IEnumerable<Deck> decks)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(",", DeckHeader));
        foreach (var d in decks)
        {
            object?[] v =
            {
                d.DeckId, d.EventId, d.DeckName, d.Player, d.Event, d.Level, d.RankRaw, d.Date,
                d.FieldSize, d.Record, d.PlacementBest, d.PlacementWorst, d.PlacementMid, d.Commander,
                d.NMainboard, d.NLands, d.NCreatures, d.NInstantSorcery, d.NOtherSpells,
                d.NUnknownType, d.DeckUrl, d.Flags,
            };
            w.WriteLine(string.Join(",", v.Select(Escape)));
        }
    }

    public static void WriteCards(string path, IEnumerable<CardEntry> cards)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(",", CardHeader));
        foreach (var c in cards)
            w.WriteLine(string.Join(",", Escape(c.DeckId), Escape(c.Qty), Escape(c.Card), Escape(c.Section)));
    }

    public static List<string[]> Parse(TextReader r)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int ch;
        while ((ch = r.Read()) != -1)
        {
            char c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (r.Peek() == '"') { sb.Append('"'); r.Read(); }
                    else inQuotes = false;
                }
                else sb.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(sb.ToString()); sb.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(sb.ToString()); sb.Clear();
                    rows.Add(row.ToArray()); row.Clear();
                    break;
                default: sb.Append(c); break;
            }
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
        return rows.Where(x => !(x.Length == 1 && x[0].Length == 0)).ToList();
    }

    static List<Dictionary<string, string>> ReadTable(string path)
    {
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = Parse(r);
        if (rows.Count == 0) return new();
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
        return rows.Skip(1).Select(cells =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++) d[header[i]] = i < cells.Length ? cells[i] : "";
            return d;
        }).ToList();
    }

    static int? Int(Dictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var v) ? (int)Math.Round(v) : null;

    static double? Dbl(Dictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;

    static string Str(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var s) ? s : "";

    public static Dataset Read(string decksPath, string cardsPath)
    {
        var decks = ReadTable(decksPath).Select(d =>
        {
            var deck = new Deck
            {
                DeckId = Str(d, "deck_id"), EventId = Str(d, "event_id"), DeckName = Str(d, "deck_name"),
                Player = Str(d, "player"), Event = Str(d, "event"), Level = Str(d, "level"),
                RankRaw = Str(d, "rank_raw"), Date = Str(d, "date"), FieldSize = Int(d, "field_size"),
                Record = Str(d, "record"), PlacementBest = Int(d, "placement_best"),
                PlacementWorst = Int(d, "placement_worst"), PlacementMid = Dbl(d, "placement_mid"),
                Commander = Str(d, "commander"), NMainboard = Int(d, "n_mainboard") ?? 0,
                NLands = Int(d, "n_lands") ?? 0, NCreatures = Int(d, "n_creatures") ?? 0,
                NInstantSorcery = Int(d, "n_instant_sorcery") ?? 0, NOtherSpells = Int(d, "n_other_spells") ?? 0,
                NUnknownType = Int(d, "n_unknown_type") ?? 0, DeckUrl = Str(d, "deck_url"), Flags = Str(d, "flags"),
            };
            // Hand-edited CSVs may lack derived columns; fill them in.
            if (deck.PlacementBest is null)
            {
                var (b, w) = Deck.ParsePlacement(deck.RankRaw);
                deck.PlacementBest = b; deck.PlacementWorst = w;
            }
            deck.PlacementMid ??= deck.PlacementBest is null ? null : (deck.PlacementBest + deck.PlacementWorst) / 2.0;
            deck.RecomputeFlags();
            return deck;
        }).ToList();

        var cards = ReadTable(cardsPath)
            .Select(d => new CardEntry(Str(d, "deck_id"), Int(d, "qty") ?? 1, Str(d, "card").Trim(), Str(d, "section")))
            .Where(c => c.Card.Length > 0)
            .ToList();

        return new Dataset { Decks = decks, Cards = cards };
    }

    /// <summary>Optional card_roles.csv: card,role (role = interaction | velocity | other).</summary>
    public static Dictionary<string, HashSet<string>> ReadRoles(string path)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in ReadTable(path))
        {
            var card = Str(d, "card").Trim();
            if (card.Length == 0) continue;
            var roles = Str(d, "role").Split(new[] { ';', '|', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r.ToLowerInvariant());
            if (!map.TryGetValue(card, out var set)) map[card] = set = new HashSet<string>();
            foreach (var r in roles) set.Add(r);
        }
        return map;
    }
}
