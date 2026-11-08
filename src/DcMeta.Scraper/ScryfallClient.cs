using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DcMeta.Core;

namespace DcMeta.Scraper;

/// <summary>
/// Looks up each card's first-ever release date (its earliest printing) on Scryfall.
/// Results are cached in a CSV so each card is only fetched once, ever.
/// Scryfall limits search/named lookups to about 2 requests/second and wants a User-Agent: https://scryfall.com/docs/api
/// </summary>
public sealed class ScryfallClient : IDisposable
{
    const string Api = "https://api.scryfall.com/";
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(550);

    readonly HttpClient _http;

    public ScryfallClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DcMeta/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose() => _http.Dispose();

    public static string DefaultCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DcMeta", "release_dates.csv");

    /// <summary>
    /// Returns release dates for every card it could resolve. Cards Scryfall does not know are cached
    /// as unknown and left out; network failures are not cached, so the next run retries them.
    /// </summary>
    public Dictionary<string, DateOnly> GetReleaseDates(IEnumerable<string> cards, string cachePath)
    {
        var cache = ReadCache(cachePath);
        var todo = cards.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !Core.Analysis.AnalysisSettings.BasicLands.Contains(c) && !cache.ContainsKey(c))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        if (todo.Count > 0)
        {
            Console.WriteLine($"Scryfall: looking up release dates for {todo.Count} cards (cached in {cachePath})");
            var failed = new List<string>();
            string? error = null;

            // 1) Batched exact-name searches: many cards per request, every printing oldest first.
            foreach (var chunk in Chunks(todo))
            {
                try
                {
                    foreach (var (card, date) in BatchSearch(chunk).GetAwaiter().GetResult())
                        cache[card] = date;
                }
                catch (Exception e) when (IsTransient(e)) { failed.AddRange(chunk); error ??= e.Message; }
            }

            // 2) Cards the exact search didn't match (e.g. MTGTop8 lists one face of a double-faced card):
            //    fuzzy name → oracle id → earliest printing. Not found at all = cached as unknown.
            var missing = todo.Where(c => !cache.ContainsKey(c) && !failed.Contains(c)).ToList();
            if (missing.Count > 0) Console.WriteLine($"  {missing.Count} cards need a fuzzy lookup");
            foreach (var card in missing)
            {
                try { cache[card] = FuzzyLookup(card).GetAwaiter().GetResult(); }
                catch (Exception e) when (IsTransient(e)) { failed.Add(card); error ??= e.Message; }
            }

            if (failed.Count > 0)
                Console.WriteLine($"  {failed.Count} lookups failed ({error}); those cards fall back to their first-seen date and are retried next run.");
            WriteCache(cachePath, cache);
        }

        return cache.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value, StringComparer.OrdinalIgnoreCase);
    }

    static bool IsTransient(Exception e) => e is HttpRequestException or TaskCanceledException or JsonException;

    /// <summary>Groups names so each OR-query stays well under Scryfall's query length limit.</summary>
    static IEnumerable<List<string>> Chunks(List<string> cards, int maxQueryChars = 800)
    {
        var chunk = new List<string>();
        int len = 0;
        foreach (var c in cards)
        {
            int add = ExactTerm(c).Length + 4;
            if (chunk.Count > 0 && len + add > maxQueryChars) { yield return chunk; chunk = new(); len = 0; }
            chunk.Add(c); len += add;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    static string ExactTerm(string card) => $"!\"{card.Replace("\"", "")}\"";

    /// <summary>
    /// One search for many cards, every printing, oldest first. The first time a name shows up is its earliest
    /// printing. Double-faced/split cards match on the full name or on either face.
    /// </summary>
    async Task<Dictionary<string, DateOnly>> BatchSearch(List<string> cards)
    {
        var wanted = new HashSet<string>(cards, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        string q = "(" + string.Join(" or ", cards.Select(ExactTerm)) + ")";
        string? next = $"{Api}cards/search?q={Uri.EscapeDataString(q)}&unique=prints&order=released&dir=asc";

        while (next is not null)
        {
            using var doc = await GetJson(next);
            if (doc is null) break; // 404 = none of these names matched
            foreach (var print in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (!print.TryGetProperty("released_at", out var r) ||
                    !DateOnly.TryParseExact(r.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                string name = print.GetProperty("name").GetString() ?? "";
                foreach (var n in name.Split(" // ").Prepend(name))
                    if (wanted.Contains(n) && (!found.TryGetValue(n, out var have) || date < have)) found[n] = date;
            }
            next = doc.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean()
                ? doc.RootElement.GetProperty("next_page").GetString() : null;
        }
        return found;
    }

    /// <summary>Fuzzy name → oracle id → earliest printing. Null if Scryfall has no such card.</summary>
    async Task<DateOnly?> FuzzyLookup(string card)
    {
        using var named = await GetJson($"{Api}cards/named?fuzzy={Uri.EscapeDataString(card)}");
        if (named is null || !named.RootElement.TryGetProperty("oracle_id", out var oid)) return null;
        using var doc = await GetJson($"{Api}cards/search?q={Uri.EscapeDataString("oracleid:" + oid.GetString())}&unique=prints&order=released&dir=asc");
        if (doc is null) return null;
        foreach (var print in doc.RootElement.GetProperty("data").EnumerateArray())
            if (print.TryGetProperty("released_at", out var r) &&
                DateOnly.TryParseExact(r.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    /// <summary>GET and parse; null on 404 (no match). On 429 (rate limited) waits as told and retries.</summary>
    async Task<JsonDocument?> GetJson(string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            await Task.Delay(Delay);
            using var resp = await _http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (resp.StatusCode == (HttpStatusCode)429 && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                await Task.Delay(wait);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        }
    }

    // Cache format: card,release_date (empty = unknown to Scryfall)
    static Dictionary<string, DateOnly?> ReadCache(string path)
    {
        var d = new Dictionary<string, DateOnly?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        foreach (var row in CsvIo.Parse(r).Skip(1))
        {
            if (row.Length < 2 || row[0].Length == 0) continue;
            d[row[0]] = DateOnly.TryParseExact(row[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        }
        return d;
    }

    static void WriteCache(string path, Dictionary<string, DateOnly?> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("card,release_date");
        foreach (var (card, date) in cache.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            w.WriteLine($"{CsvIo.Escape(card)},{date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
    }
}
