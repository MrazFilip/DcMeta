using System.Text;
using DcMeta.Core;
using DcMeta.Core.Analysis;

namespace DcMeta.Analyzer;

/// <summary>
/// Plain decklist: one "qty Card Name" per line, no headers or blank lines, so it pastes into
/// Moxfield, Archidekt, MTGGoldfish, Cockatrice, MTGO and most other deck sites. Commander excluded.
/// </summary>
public static class DeckListText
{
    static readonly string[] Order = { Sections.Creature, Sections.InstantSorcery, Sections.Other, Sections.Land, Sections.Unknown };

    public static string Build(IEnumerable<RecommendedCard> cards) =>
        string.Concat(cards
            .OrderBy(c => Rank(c.Category))
            .ThenBy(c => c.Card, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Qty} {c.Card}\n"));

    static int Rank(string category)
    {
        int i = Array.IndexOf(Order, category);
        return i < 0 ? Order.Length : i;
    }

    public static void Write(string path, IEnumerable<RecommendedCard> cards) =>
        File.WriteAllText(path, Build(cards), new UTF8Encoding(false));
}
