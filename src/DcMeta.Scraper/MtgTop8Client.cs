using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DcMeta.Core;
using HtmlAgilityPack;

namespace DcMeta.Scraper;

public sealed record DeckListing(string DeckId, string EventId, string DeckName, string Player,
                                 string Event, string Level, string RankRaw, string Date);

/// <summary>
/// Minimal MTGTop8 client. MTGTop8 HTML changes now and then; if a parser returns nothing,
/// inspect one page in the browser (F12) and adjust the lines marked SELECTOR.
/// </summary>
public sealed class MtgTop8Client : IDisposable
{
    public const string Base = "https://www.mtgtop8.com/";

    static readonly (string Header, string Category)[] SectionMap =
    {
        ("LANDS", Sections.Land),
        ("CREATURES", Sections.Creature),
        ("INSTANTS and SORC.", Sections.InstantSorcery),
        ("OTHER SPELLS", Sections.Other),
        ("COMMANDER", Sections.Commander),
        ("SIDEBOARD", Sections.Commander),  // DC lists usually store the commander here
    };

    readonly HttpClient _http;
    readonly TimeSpan _delay;
    readonly string _format;

    public MtgTop8Client(TimeSpan delay, string format = "EDH")
    {
        _delay = delay;
        _format = format;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (DC research scraper)");
    }

    public void Dispose() => _http.Dispose();

    async Task<string> GetString(string url, Dictionary<string, string>? form = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            await Task.Delay(_delay);
            try
            {
                using var resp = form is null
                    ? await _http.GetAsync(url)
                    : await _http.PostAsync(url, new FormUrlEncodedContent(form));
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var charset = resp.Content.Headers.ContentType?.CharSet;
                Encoding enc;
                try { enc = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
                catch { enc = Encoding.UTF8; }
                return enc.GetString(bytes);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    async Task<HtmlDocument> GetDoc(string url, Dictionary<string, string>? form = null)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(await GetString(url, form));
        return doc;
    }

    static string Clean(string s) => Regex.Replace(HtmlEntity.DeEntitize(s), @"\s+", " ").Trim();

    /// <summary>Walks the archetype listing. <paramref name="url"/> is the full archetype URL with all its filters.</summary>
    public async Task<List<DeckListing>> ListDecks(string url, int pages)
    {
        var rows = new List<DeckListing>();
        var seen = new HashSet<string>();
        var linkRx = new Regex(@"e=(\d+)&(?:amp;)?d=(\d+)");

        for (int page = 1; page <= pages; page++)
        {
            // Paging is the page's "nav_form": POST current_page=N to the same URL (PageSubmit_arch). SELECTOR
            var doc = page == 1 ? await GetDoc(url) : await GetDoc(url, new() { ["current_page"] = page.ToString() });

            int found = 0;
            // Each deck is a <tr class="hover_tr"> with 7 cells:
            // [checkbox] [deck] [player] [event] [level = star images] [rank] [date]. SELECTOR
            var trs = doc.DocumentNode.SelectNodes("//tr[contains(concat(' ',normalize-space(@class),' '),' hover_tr ')]")
                      ?? Enumerable.Empty<HtmlNode>();
            foreach (var tr in trs)
            {
                var a = tr.SelectSingleNode(".//a[contains(@href,'event?e=') and contains(@href,'d=')]");
                if (a is null) continue;
                var m = linkRx.Match(a.GetAttributeValue("href", ""));
                if (!m.Success) continue;
                string eventId = m.Groups[1].Value, deckId = m.Groups[2].Value;
                if (!seen.Add(deckId)) continue;

                var tds = tr.SelectNodes("./td")?.ToList() ?? new List<HtmlNode>();
                if (tds.Count < 7) continue; // not a deck row
                string C(int i) => Clean(tds[i].InnerText);
                string level = (tds[4].SelectNodes(".//img")?.Count ?? 0).ToString(); // stars
                rows.Add(new DeckListing(deckId, eventId, C(1), C(2), C(3), level, C(5), C(6)));
                found++;
            }
            Term.Dim($"page {page}: {found} decks");
            if (found == 0) break;
        }
        return rows;
    }

    public async Task<(int? FieldSize, string Record, List<(int Qty, string Card, string Section)> Cards)>
        GetDeck(string eventId, string deckId)
    {
        var doc = await GetDoc($"{Base}event?e={eventId}&d={deckId}&f={_format}");
        string text = Clean(doc.DocumentNode.InnerText);

        // Field size: the first "N players - dd/mm/yy" on the page is this event (others are sidebar events). SELECTOR
        var m = Regex.Match(text, @"(\d+)\s+players", RegexOptions.IgnoreCase);
        int? fieldSize = m.Success ? int.Parse(m.Groups[1].Value) : null;

        // Record (only some events publish it), e.g. "(5-1-0)"
        m = Regex.Match(text, @"\((\d+-\d+(?:-\d+)?)\)");
        string record = m.Success ? m.Groups[1].Value : "";

        // Card lines grouped under section headers, in document order. SELECTOR
        var cards = new List<(int, string, string)>();
        string? section = null;
        var nodes = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ',normalize-space(@class),' '),' O14 ')" +
            " or contains(concat(' ',normalize-space(@class),' '),' deck_line ')]")
            ?? Enumerable.Empty<HtmlNode>();
        var lineRx = new Regex(@"^(\d+)\s+(.+)$");
        foreach (var el in nodes)
        {
            string t = Clean(el.InnerText);
            if (el.GetClasses().Contains("O14"))
            {
                // Headers look like "COMMANDER", "38 LANDS (39)", "30 CREATURES". Other O14 divs
                // (prices, totals) are not sections and leave the current section unchanged.
                string hdr = Regex.Replace(t, @"^\d+\s*", "").Trim();
                var known = SectionMap.Where(s => hdr.StartsWith(s.Header, StringComparison.OrdinalIgnoreCase))
                                      .Select(s => s.Category).FirstOrDefault();
                if (known is not null) section = known;
                continue;
            }
            var lm = lineRx.Match(t);
            if (lm.Success && section is not null)
                cards.Add((int.Parse(lm.Groups[1].Value), lm.Groups[2].Value.Trim(), section));
        }

        if (cards.Count == 0) cards = await GetMtgoExport(deckId); // no type info in this fallback
        return (fieldSize, record, cards);
    }

    async Task<List<(int, string, string)>> GetMtgoExport(string deckId)
    {
        string body = await GetString($"{Base}mtgo?d={deckId}");
        var cards = new List<(int, string, string)>();
        string section = Sections.Unknown;
        foreach (var raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("sideboard", StringComparison.OrdinalIgnoreCase)) { section = Sections.Commander; continue; }
            var m = Regex.Match(line, @"^(\d+)\s+(.+)$");
            if (m.Success) cards.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value, section));
        }
        return cards;
    }
}
