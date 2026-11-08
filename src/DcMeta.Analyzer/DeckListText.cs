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
    // Categories always come from Sections.Mainboard, so IndexOf never misses.
    public static void Write(string path, IEnumerable<RecommendedCard> cards) =>
        File.WriteAllText(path, string.Concat(cards
            .OrderBy(c => Array.IndexOf(Sections.Mainboard, c.Category))
            .ThenBy(c => c.Card, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Qty} {c.Card}\n")), new UTF8Encoding(false));
}
