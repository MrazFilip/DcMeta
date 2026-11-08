using System.Globalization;

namespace DcMeta.Core.Analysis;

/// <summary>
/// Runs the full Master-Prompt pipeline on a scraped dataset:
/// tournament coefficients → archetypes → card metrics → Recommended 99 → cut list →
/// cross-archetype comparison → statistical summary. Pure computation, no I/O.
/// </summary>
public sealed class MetaAnalyzer
{
    readonly AnalysisSettings _s;
    readonly Dictionary<string, HashSet<string>>? _roles;
    readonly Dictionary<string, DateOnly> _releaseDates;
    readonly Dictionary<string, (DateOnly From, string Source)> _availableFrom = new();

    /// <param name="releaseDates">Card → first release date (e.g. from Scryfall). Only used with availability windows;
    /// cards missing from it fall back to the first date they appear in the data.</param>
    public MetaAnalyzer(AnalysisSettings settings, Dictionary<string, HashSet<string>>? roles = null,
                        IReadOnlyDictionary<string, DateOnly>? releaseDates = null)
    {
        _s = settings;
        _roles = roles is { Count: > 0 } ? roles : null;
        _releaseDates = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (card, date) in releaseDates ?? new Dictionary<string, DateOnly>())
            _releaseDates[NormalizeName(card)] = date;
    }

    public AnalysisResult Run(Dataset data)
    {
        var r = new AnalysisResult { Settings = _s, Data = data, HasRoles = _roles is not null };

        var cardsByDeck = data.Cards
            .Where(c => c.Section != Sections.Commander)
            .GroupBy(c => (c.DeckId, Card: NormalizeName(c.Card)))
            .Select(g => new CardEntry(g.Key.DeckId, g.Sum(x => x.Qty), g.Key.Card, g.First().Section))
            .ToLookup(c => c.DeckId);

        ScoreDecks(r, cardsByDeck);
        if (r.Decks.Count == 0) return r;
        if (_s.RecencyHalfLifeDays is double hl && hl > 0) ApplyRecency(r, hl);

        AssignArchetypes(r, cardsByDeck);
        if (_s.AvailabilityWindows) ComputeAvailability(r, cardsByDeck);

        var modeSection = ModeSections(r.Decks, cardsByDeck);
        r.Cards = ComputeCardMetrics(r.Decks, cardsByDeck, modeSection);
        r.CardsCreatureBased = ComputeCardMetrics(r.Decks.Where(d => d.Archetype == Archetypes.CreatureBased).ToList(), cardsByDeck, modeSection);
        r.CardsCreatureLight = ComputeCardMetrics(r.Decks.Where(d => d.Archetype == Archetypes.CreatureLight).ToList(), cardsByDeck, modeSection);
        var high = r.Decks.Where(d => (d.Deck.FieldSize ?? 0) >= _s.HighFieldThreshold).ToList();
        r.HighFieldDecks = high.Count;
        r.CardsHighField = ComputeCardMetrics(high, cardsByDeck, modeSection);
        r.WindowedCards = r.Cards.Count(c => c.IsWindowed);

        BuildRecommended(r, cardsByDeck, modeSection);
        BuildCutList(r);
        BuildCross(r);
        BuildStructure(r, cardsByDeck, modeSection);
        BuildSummary(r);
        return r;
    }

    public static string NormalizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");

    // =================================================================== 1. Tournament coefficient
    public double PlacementWeight(double placementMid, int fieldSize)
    {
        if (_s.WeightMode == PlacementWeightMode.Log)
        {
            double lf = Math.Log2(Math.Max(2, fieldSize));
            double w = (lf - Math.Log2(Math.Max(1, placementMid)) + 1) / (lf + 1);
            return Math.Clamp(w, 0.05, 1.0);
        }
        foreach (var (maxMid, weight) in _s.PlacementTable)
            if (placementMid <= maxMid) return weight;
        return _s.PlacementTable[^1].Weight;
    }

    void ScoreDecks(AnalysisResult r, ILookup<string, CardEntry> cardsByDeck)
    {
        foreach (var d in r.Data.Decks)
        {
            string? reason = null;
            if (!cardsByDeck[d.DeckId].Any()) reason = "no card rows";
            else if (d.FieldSize is null) reason = "missing field size";
            else if (d.PlacementMid is null) reason = "missing placement";
            else if (d.IsLeague && !_s.IncludeLeagues) reason = "MTGO league (rank is a record, not a placement)";
            else if (d.FieldSize < _s.MinFieldSize) reason = $"field size {d.FieldSize} < {_s.MinFieldSize}";
            else if (_s.Strict99 && d.NMainboard != 99) reason = $"mainboard {d.NMainboard} ≠ 99 (strict mode)";
            else if (d.PlacementMid > d.FieldSize) reason = "placement exceeds field size";

            r.Audit.Add(new AuditRow(d, reason is null, reason ?? "included" + (d.Flags.Length > 0 ? $" (flags: {d.Flags})" : "")));
            if (reason is not null) continue;

            double ln = Math.Log(d.FieldSize!.Value);
            double pw = PlacementWeight(d.PlacementMid!.Value, d.FieldSize.Value);
            r.Decks.Add(new ScoredDeck { Deck = d, LnField = ln, PlacementWeight = pw, C = ln * pw });
        }
    }

    /// <summary>Recency = 0.5^(days before the newest deck / half-life). Undated decks keep weight 1.</summary>
    static void ApplyRecency(AnalysisResult r, double halfLifeDays)
    {
        var dates = r.Decks.Select(d => d.Deck.ParsedDate).OfType<DateOnly>().ToList();
        if (dates.Count == 0)
        {
            r.RecommendedNotes.Add("Recency weighting is on, but no deck has a readable date; all decks count equally.");
            return;
        }
        DateOnly newest = dates.Max();
        foreach (var d in r.Decks)
            if (d.Deck.ParsedDate is { } dt)
                d.Recency = Math.Pow(0.5, (newest.DayNumber - dt.DayNumber) / halfLifeDays);
    }

    // =================================================================== 2. Archetypes
    void AssignArchetypes(AnalysisResult r, ILookup<string, CardEntry> cardsByDeck)
    {
        foreach (var sd in r.Decks)
        {
            var cards = cardsByDeck[sd.Deck.DeckId].ToList();
            int main = Math.Max(1, cards.Sum(c => c.Qty));
            int creatures = cards.Where(c => c.Section == Sections.Creature).Sum(c => c.Qty);
            int spells = cards.Where(c => c.Section is Sections.InstantSorcery or Sections.Other).Sum(c => c.Qty);
            sd.CreatureDensity = creatures / (double)main;
            sd.SpellDensity = spells / (double)main;
            if (_roles is not null)
            {
                sd.InteractionDensity = cards.Where(c => HasRole(c.Card, "interaction")).Sum(c => c.Qty) / (double)main;
                sd.VelocityDensity = cards.Where(c => HasRole(c.Card, "velocity")).Sum(c => c.Qty) / (double)main;
            }
        }

        double Creatures(ScoredDeck d) => d.CreatureDensity * Math.Max(1, cardsByDeck[d.Deck.DeckId].Sum(c => c.Qty));
        var creatureCounts = r.Decks.Select(Creatures).ToList();

        double t = _s.CreatureThreshold ?? Stats.OneDimTwoMeansThreshold(creatureCounts);
        r.CreatureThresholdUsed = t;
        var labels = creatureCounts.Select(c => c >= t ? 1 : 0).ToArray();
        List<double[]> points;

        if (_s.CreatureThreshold is not null)
        {
            r.ClusteringMethod = $"Manual threshold: ≥ {t:0} creatures = {Archetypes.CreatureBased}";
            points = creatureCounts.Select(c => new[] { c }).ToList();
        }
        else if (_roles is null)
        {
            r.ClusteringMethod = $"Exact 1-D 2-means on creature count (split at ≥ {t:0} creatures). " +
                                 "Interaction/velocity densities need card_roles.csv.";
            points = creatureCounts.Select(c => new[] { c }).ToList();
        }
        else
        {
            var raw = r.Decks.Select(d => new[] { d.CreatureDensity, d.SpellDensity, d.InteractionDensity ?? 0, d.VelocityDensity ?? 0 }).ToList();
            points = Standardize(raw);
            labels = Stats.KMeans(points, labels, 2);
            // Cluster with the higher mean creature density is Creature-based.
            double m0 = Enumerable.Range(0, labels.Length).Where(i => labels[i] == 0).Select(i => creatureCounts[i]).DefaultIfEmpty(0).Average();
            double m1 = Enumerable.Range(0, labels.Length).Where(i => labels[i] == 1).Select(i => creatureCounts[i]).DefaultIfEmpty(0).Average();
            if (m0 > m1) labels = labels.Select(l => 1 - l).ToArray();
            r.CreatureThresholdUsed = (m0 + m1) / 2;
            r.ClusteringMethod = "2-means on standardized creature, spell, interaction and velocity densities " +
                                 "(seeded by the 1-D creature split).";
        }

        var sil = PerPointSilhouette(points, labels);
        for (int i = 0; i < r.Decks.Count; i++)
        {
            var d = r.Decks[i];
            d.Archetype = labels[i] == 1 ? Archetypes.CreatureBased : Archetypes.CreatureLight;
            d.Silhouette = sil[i];
            d.Borderline = _roles is null || _s.CreatureThreshold is not null
                ? creatureCounts[i] >= t - _s.BorderlineBand && creatureCounts[i] < t + _s.BorderlineBand
                : sil[i] < 0.1;
        }

        double total = r.Decks.Count;
        foreach (var g in r.Decks.GroupBy(d => d.Archetype).OrderBy(g => g.Key))
        {
            var cs = g.Select(d => d.C).ToList();
            var cr = g.Select(Creatures).ToList();
            r.ArchetypeStats.Add(new ArchetypeStats(
                g.Key, g.Count(), 100.0 * g.Count() / total, cs.Average(), Stats.Sd(cs), Stats.Median(cs), cs.Sum(),
                cr.Average(), Stats.Sd(cr), (int)cr.Min(), (int)cr.Max(),
                g.Average(d => d.SpellDensity),
                _roles is null ? null : g.Average(d => d.InteractionDensity ?? 0),
                _roles is null ? null : g.Average(d => d.VelocityDensity ?? 0),
                g.Average(d => d.Silhouette), 100.0 * g.Count(d => d.Borderline) / g.Count()));
        }
    }

    bool HasRole(string card, string role) => _roles!.TryGetValue(card, out var set) && set.Contains(role);

    static List<double[]> Standardize(List<double[]> raw)
    {
        int dim = raw[0].Length;
        var mean = Enumerable.Range(0, dim).Select(j => raw.Average(x => x[j])).ToArray();
        var sd = Enumerable.Range(0, dim).Select(j => { var s = Stats.Sd(raw.Select(x => x[j])); return double.IsNaN(s) || s == 0 ? 1 : s; }).ToArray();
        return raw.Select(x => x.Select((v, j) => (v - mean[j]) / sd[j]).ToArray()).ToList();
    }

    static double[] PerPointSilhouette(IList<double[]> pts, int[] labels)
    {
        var res = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            double a = MeanDist(i, labels[i]), b = MeanDist(i, 1 - labels[i]);
            res[i] = double.IsNaN(a) || double.IsNaN(b) || Math.Max(a, b) == 0 ? 0 : (b - a) / Math.Max(a, b);
        }
        return res;

        double MeanDist(int i, int label)
        {
            double sum = 0; int n = 0;
            for (int j = 0; j < pts.Count; j++)
                if (j != i && labels[j] == label) { sum += Math.Sqrt(pts[i].Zip(pts[j], (p, q) => (p - q) * (p - q)).Sum()); n++; }
            return n == 0 ? double.NaN : sum / n;
        }
    }

    // =================================================================== Availability windows
    /// <summary>
    /// Each card's window starts at its release date when known, else at the first included deck that plays it.
    /// Cards available before the oldest deck get no entry (their window is the whole dataset).
    /// </summary>
    void ComputeAvailability(AnalysisResult r, ILookup<string, CardEntry> cardsByDeck)
    {
        var dated = r.Decks.Where(d => d.Deck.ParsedDate is not null).ToList();
        if (dated.Count == 0)
        {
            r.RecommendedNotes.Add("Availability windows are on, but no deck has a readable date; every card uses the whole dataset.");
            return;
        }
        DateOnly oldest = dated.Min(d => d.Deck.ParsedDate!.Value);

        var firstSeen = dated
            .SelectMany(d => cardsByDeck[d.Deck.DeckId].Select(c => (c.Card, Date: d.Deck.ParsedDate!.Value)))
            .GroupBy(x => x.Card)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Date));

        foreach (var (card, seen) in firstSeen)
        {
            var (from, source) = _releaseDates.TryGetValue(card, out var rel) ? (rel, "Scryfall") : (seen, "first seen");
            if (from > oldest) _availableFrom[card] = (from, source);
        }
    }

    // =================================================================== 3./4. Card metrics
    static Dictionary<string, string> ModeSections(List<ScoredDeck> decks, ILookup<string, CardEntry> cardsByDeck)
    {
        return decks.SelectMany(d => cardsByDeck[d.Deck.DeckId])
            .GroupBy(c => c.Card)
            .ToDictionary(g => g.Key, g =>
            {
                var typed = g.Where(c => c.Section != Sections.Unknown).ToList();
                var pool = typed.Count > 0 ? typed : g.ToList();
                return pool.GroupBy(c => c.Section).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key;
            });
    }

    public List<CardMetric> ComputeCardMetrics(IList<ScoredDeck> decks, ILookup<string, CardEntry> cardsByDeck,
                                               IReadOnlyDictionary<string, string> modeSection)
    {
        if (decks.Count == 0) return new();
        // Value side (Inclusion, Density) uses W = C × recency weight; Avg_C stays on plain C, so it keeps
        // meaning "how strong are the decks that play this card". Without --half-life, W = C.
        double totalC = decks.Sum(d => d.W);
        double meanW = decks.Average(d => d.W);
        double meanC = decks.Average(d => d.C);
        int n = decks.Count;

        var list = decks
            .SelectMany(d => cardsByDeck[d.Deck.DeckId].Select(c => (Deck: d, c.Card, c.Qty)))
            .GroupBy(x => x.Card)
            .Select(g =>
            {
                var present = g.GroupBy(x => x.Deck.Deck.DeckId).Select(x => (x.First().Deck, Qty: x.Sum(y => y.Qty))).ToList();
                double incl = present.Sum(p => p.Deck.W);
                double sumC = present.Sum(p => p.Deck.C);
                var top = present.OrderByDescending(p => p.Deck.C).Take(3)
                    .Select(p => $"{p.Deck.Deck.Label} C={p.Deck.C.ToString("0.00", CultureInfo.InvariantCulture)}");

                // Window: decks dated on/after the card became available. Undated decks and decks that
                // actually play the card always count (guards against a wrong or late release date).
                int windowN = n;
                double windowC = totalC, density = totalC == 0 ? 0 : incl / totalC;
                bool hasWindow = _availableFrom.TryGetValue(g.Key, out var avail);
                if (hasWindow)
                {
                    var ids = present.Select(p => p.Deck.Deck.DeckId).ToHashSet();
                    var window = decks.Where(d => d.Deck.ParsedDate is not { } dt || dt >= avail.From || ids.Contains(d.Deck.DeckId)).ToList();
                    windowN = window.Count;
                    windowC = window.Sum(d => d.W);
                    // Shrink: k pseudo-decks of average weight at the card's whole-dataset rate, so 5/5 decks
                    // ranks high but below 80/100.
                    double k = _s.WindowShrinkK * meanW;
                    if (windowN < n) density = windowC + k == 0 ? 0 : (incl + k * density) / (windowC + k);
                }
                return new CardMetric
                {
                    Card = g.Key,
                    Section = modeSection.GetValueOrDefault(g.Key, Sections.Unknown),
                    DecksPresent = present.Count,
                    WindowDecks = windowN,
                    AvailableFrom = hasWindow ? avail.From : null,
                    AvailableSource = hasWindow ? avail.Source : "",
                    IsWindowed = windowN < n,
                    Popularity = present.Count / (double)windowN,
                    Inclusion = incl,
                    Density = density,
                    AvgC = sumC / present.Count,
                    MedianQty = Stats.Median(present.Select(p => (double)p.Qty)),
                    TopExamples = string.Join(" | ", top),
                    CValues = present.Select(p => p.Deck.C).ToList(),
                };
            }).ToList();

        double maxIncl = list.Max(c => c.Inclusion), maxDens = list.Max(c => c.Density);
        double meanAvg = list.Average(c => c.AvgC), sdAvg = Stats.Sd(list.Select(c => c.AvgC));
        double wSum = _s.InclusionWeight + _s.DensityWeight;
        foreach (var c in list)
        {
            // With windows, raw Inclusion still sums C over the whole dataset (penalising new cards), so the
            // normalized column uses window-projected inclusion (Density × total C) instead — i.e. Density_norm.
            c.InclusionNorm = _s.AvailabilityWindows ? (maxDens == 0 ? 0 : c.Density / maxDens)
                            : maxIncl == 0 ? 0 : c.Inclusion / maxIncl;
            c.DensityNorm = maxDens == 0 ? 0 : c.Density / maxDens;
            c.Value = (_s.InclusionWeight * c.InclusionNorm + _s.DensityWeight * c.DensityNorm) / wSum;
            c.AvgCShrunk = (c.DecksPresent * c.AvgC + _s.ShrinkageK * meanC) / (c.DecksPresent + _s.ShrinkageK);
            c.AvgCZ = double.IsNaN(sdAvg) || sdAvg == 0 ? 0 : (c.AvgC - meanAvg) / sdAvg;
        }
        return list.OrderByDescending(c => c.Value).ThenByDescending(c => c.AvgC).ThenBy(c => c.Card).ToList();
    }

    // =================================================================== 6. Recommended 99
    double SelectionScore(CardMetric c, double minShr, double maxShr)
    {
        double shrNorm = maxShr > minShr ? (c.AvgCShrunk - minShr) / (maxShr - minShr) : 0.5;
        return (1 - _s.AvgCRefinement) * c.Value + _s.AvgCRefinement * shrNorm;
    }

    readonly Dictionary<string, double> _score = new();

    void BuildRecommended(AnalysisResult r, ILookup<string, CardEntry> cardsByDeck, Dictionary<string, string> modeSection)
    {
        int nDecks = r.Decks.Count;
        int floor = _s.StabilityFloor;
        if (floor > nDecks * 0.5)
        {
            int adj = Math.Max(1, (int)Math.Ceiling(nDecks * 0.3));
            r.RecommendedNotes.Add($"Stability floor {floor} exceeds half the dataset ({nDecks} decks); " +
                                   $"lowered to {adj} (30% of decks).");
            floor = adj;
        }
        r.EffectiveFloor = floor;

        double minShr = r.Cards.Min(c => c.AvgCShrunk), maxShr = r.Cards.Max(c => c.AvgCShrunk);
        foreach (var c in r.Cards) _score[c.Card] = SelectionScore(c, minShr, maxShr);

        // Section quotas = median per-deck count by (mode) section, rebalanced to sum to 99.
        var perDeck = r.Decks.Select(d => cardsByDeck[d.Deck.DeckId]
            .GroupBy(c => modeSection.GetValueOrDefault(c.Card, Sections.Unknown))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Qty))).ToList();
        var sections = Sections.Mainboard.Where(s => perDeck.Any(p => p.ContainsKey(s))).ToList();
        foreach (var s in sections)
            r.SectionQuotas[s] = (int)Math.Round(Stats.Median(perDeck.Select(p => (double)p.GetValueOrDefault(s, 0))), MidpointRounding.AwayFromZero);
        int diff = 99 - r.SectionQuotas.Values.Sum();
        if (diff != 0)
        {
            string adjust = r.SectionQuotas.ContainsKey(Sections.Land) && r.SectionQuotas[Sections.Land] + diff >= 0
                ? Sections.Land : r.SectionQuotas.OrderByDescending(kv => kv.Value).First().Key;
            r.SectionQuotas[adjust] += diff;
            r.RecommendedNotes.Add($"Section medians summed to {99 - diff}; adjusted {adjust} by {diff:+0;-0} to reach 99.");
        }

        double meanC = r.Decks.Average(d => d.C), sdC = Stats.Sd(r.Decks.Select(d => d.C));
        foreach (var section in sections)
        {
            int quota = r.SectionQuotas[section];
            var pool = r.Cards.Where(c => c.Section == section).OrderByDescending(c => _score[c.Card]).ThenBy(c => c.Card).ToList();
            var chosen = new HashSet<string>();
            int remaining = quota, f = floor;
            while (remaining > 0 && f >= 1)
            {
                foreach (var c in pool.Where(c => c.DecksPresent >= r.CardFloor(c, f) && !chosen.Contains(c.Card)))
                {
                    if (remaining <= 0) break;
                    bool basic = AnalysisSettings.BasicLands.Contains(c.Card);
                    int qty = basic ? Math.Max(1, (int)Math.Round(c.MedianQty, MidpointRounding.AwayFromZero)) : 1;
                    qty = Math.Min(qty, remaining);
                    chosen.Add(c.Card);
                    remaining -= qty;
                    int rank = pool.IndexOf(c) + 1;
                    r.Recommended.Add(new RecommendedCard(c.Card, section, qty, c.Value, c.AvgC, c.AvgCShrunk, c.DecksPresent,
                        _score[c.Card], f, Reason(c, section, rank, pool.Count, nDecks, meanC, sdC, f, floor, basic, qty)));
                }
                if (remaining > 0)
                {
                    if (f == floor)
                        r.RecommendedNotes.Add($"{section}: only {chosen.Count} cards meet the floor of {floor}; relaxing the floor to fill {quota} slots.");
                    f--;
                }
            }
            if (remaining > 0)
                r.RecommendedNotes.Add($"{section}: {remaining} slot(s) left unfilled — not enough distinct cards in the dataset.");
        }
    }

    static string Reason(CardMetric c, string section, int rank, int poolSize, int nDecks, double meanC, double sdC,
                         int floorUsed, int floor, bool basic, int qty)
    {
        var parts = new List<string>
        {
            $"#{rank}/{poolSize} {section} by score",
            c.IsWindowed
                ? $"new card: available since {c.AvailableFrom:yyyy-MM-dd} ({c.AvailableSource}), in {c.DecksPresent}/{c.WindowDecks} decks since ({c.Popularity:P0})"
                : $"in {c.DecksPresent}/{nDecks} decks ({c.Popularity:P0})",
            $"Value {c.Value:0.000}",
            $"Avg_C {c.AvgC:0.00} vs mean {meanC:0.00} ({c.AvgC - meanC:+0.00;-0.00})",
        };
        if (c.Popularity >= 0.8) parts.Add("core staple (≥80% of decks)");
        else if (!double.IsNaN(sdC) && c.AvgC - meanC >= 0.5 * sdC) parts.Add("concentrated in high-C decks");
        if (basic) parts.Add($"basic land ×{qty} (median copies)");
        if (floorUsed < floor) parts.Add($"floor relaxed to {floorUsed} to fill {section} quota");
        return string.Join("; ", parts);
    }

    // =================================================================== 7. Cut list
    void BuildCutList(AnalysisResult r)
    {
        var rec = r.Recommended.Select(x => x.Card).ToHashSet();
        double medianValue = Stats.Median(r.Cards.Select(c => c.Value));
        double meanC = r.Decks.Average(d => d.C), sdC = Stats.Sd(r.Decks.Select(d => d.C));
        var cutoff = r.Recommended.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Min(x => x.Score));

        foreach (var c in r.Cards.Where(c => c.DecksPresent >= _s.CutListMinDecks && !rec.Contains(c.Card)))
        {
            double score = _score[c.Card];
            string verdict, reason;
            int cardFloor = r.CardFloor(c, r.EffectiveFloor);
            if (c.DecksPresent >= cardFloor && cutoff.TryGetValue(c.Section, out var cut))
            {
                verdict = "Cut (structural redundancy)";
                reason = $"Meets the stability floor but {c.Section} slots are filled by higher-scoring cards (score {score:0.000} < cutoff {cut:0.000}).";
            }
            else if (c.Value < medianValue && c.AvgC < meanC)
            {
                verdict = "Cut (low Value, low Avg_C)";
                reason = $"Value {c.Value:0.000} below median {medianValue:0.000} and Avg_C {c.AvgC:0.00} below deck mean {meanC:0.00}.";
            }
            else if (!double.IsNaN(sdC) && c.AvgC >= meanC + 0.5 * sdC)
            {
                verdict = "Watch (small-sample high Avg_C)";
                reason = $"Avg_C {c.AvgC:0.00} is ≥0.5 SD above mean but only {c.DecksPresent} decks; shrunk Avg_C {c.AvgCShrunk:0.00}. Not enough support to include.";
            }
            else
            {
                verdict = "Cut (low support)";
                reason = $"Below the stability floor ({c.DecksPresent} < {cardFloor}) with no compensating Avg_C signal.";
            }
            if (c.IsWindowed)
                reason += $" New card: available since {c.AvailableFrom:yyyy-MM-dd} ({c.AvailableSource}), in {c.DecksPresent}/{c.WindowDecks} decks since.";
            r.CutList.Add(new CutCard(c.Card, c.Section, verdict, c.Value, c.AvgC, c.DecksPresent, score, reason));
        }
        r.CutList.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    // =================================================================== 8. Cross-archetype
    void BuildCross(AnalysisResult r)
    {
        double sdC = Stats.Sd(r.Decks.Select(d => d.C));
        double thr = _s.CrossDeltaThreshold ?? (double.IsNaN(sdC) ? 0.5 : 0.5 * sdC);
        r.CrossThresholdUsed = thr;
        var cb = r.CardsCreatureBased.ToDictionary(c => c.Card);
        var cl = r.CardsCreatureLight.ToDictionary(c => c.Card);

        foreach (var all in r.Cards)
        {
            cb.TryGetValue(all.Card, out var a);
            cl.TryGetValue(all.Card, out var b);
            double? delta = a is not null && b is not null ? a.AvgC - b.AvgC : null;
            double? t = a is not null && b is not null ? Stats.WelchT(a.CValues.ToList(), b.CValues.ToList()) : null;
            if (t is double tv && double.IsNaN(tv)) t = null;
            bool enough = (a?.DecksPresent ?? 0) >= _s.CrossMinDecksPerSide && (b?.DecksPresent ?? 0) >= _s.CrossMinDecksPerSide;
            bool sig = enough && delta is double dv && Math.Abs(dv) >= thr;
            string note = a is null ? $"only in {Archetypes.CreatureLight}"
                : b is null ? $"only in {Archetypes.CreatureBased}"
                : !enough ? $"< {_s.CrossMinDecksPerSide} decks on one side"
                : sig ? (delta > 0 ? $"stronger in {Archetypes.CreatureBased}" : $"stronger in {Archetypes.CreatureLight}")
                : "similar";
            r.Cross.Add(new CrossRow(all.Card, all.Section, a?.DecksPresent ?? 0, a?.AvgC, b?.DecksPresent ?? 0, b?.AvgC, delta, t, sig, note));
        }
        r.Cross.Sort((x, y) => Math.Abs(y.Delta ?? 0).CompareTo(Math.Abs(x.Delta ?? 0)));
    }

    // =================================================================== Structural check
    void BuildStructure(AnalysisResult r, ILookup<string, CardEntry> cardsByDeck, Dictionary<string, string> modeSection)
    {
        foreach (var s in r.SectionQuotas.Keys)
        {
            var counts = r.Decks.Select(d => (double)cardsByDeck[d.Deck.DeckId]
                .Where(c => modeSection.GetValueOrDefault(c.Card, Sections.Unknown) == s).Sum(c => c.Qty)).ToList();
            r.Structure.Add(new StructureRow($"{s} count", Stats.Median(counts), counts.Average(),
                r.Recommended.Where(x => x.Category == s).Sum(x => x.Qty)));
        }
        if (_roles is not null)
        {
            foreach (var role in new[] { "interaction", "velocity" })
            {
                var counts = r.Decks.Select(d => (double)cardsByDeck[d.Deck.DeckId].Where(c => HasRole(c.Card, role)).Sum(c => c.Qty)).ToList();
                r.Structure.Add(new StructureRow($"{role} count", Stats.Median(counts), counts.Average(),
                    r.Recommended.Where(x => HasRole(x.Card, role)).Sum(x => x.Qty)));
            }
        }
    }

    // =================================================================== 9. Statistical summary
    void BuildSummary(AnalysisResult r)
    {
        var s = r.Summary;
        var avg = r.Cards.Select(c => c.AvgC).ToList();
        s.MinAvgC = avg.Min(); s.MaxAvgC = avg.Max(); s.RangeAvgC = s.MaxAvgC - s.MinAvgC; s.SdAvgC = Stats.Sd(avg);

        var stable = r.Cards.Where(c => c.DecksPresent >= r.CardFloor(c, r.EffectiveFloor)).ToList();
        s.StableCards = stable.Count;
        if (stable.Count > 0)
        {
            s.MinAvgCStable = stable.Min(c => c.AvgC); s.MaxAvgCStable = stable.Max(c => c.AvgC);
            s.RangeAvgCStable = s.MaxAvgCStable - s.MinAvgCStable; s.SdAvgCStable = Stats.Sd(stable.Select(c => c.AvgC));
        }

        var cs = r.Decks.Select(d => d.C).ToList();
        s.MeanDeckC = cs.Average(); s.SdDeckC = Stats.Sd(cs); s.MinDeckC = cs.Min(); s.MaxDeckC = cs.Max();

        s.RefDelta = _s.ReferenceDelta;
        s.RefPctOfRange = s.RangeAvgC > 0 ? 100 * s.RefDelta / s.RangeAvgC : double.NaN;
        s.RefPctOfRangeStable = s.RangeAvgCStable > 0 ? 100 * s.RefDelta / s.RangeAvgCStable : double.NaN;
        s.RefInDeckSd = s.SdDeckC > 0 ? s.RefDelta / s.SdDeckC : double.NaN;
        s.TypicalN = r.Recommended.Count > 0 ? Stats.Median(r.Recommended.Select(x => (double)x.DecksPresent)) : Stats.Median(r.Cards.Select(c => (double)c.DecksPresent));
        s.SeAtTypicalN = s.TypicalN > 0 ? s.SdDeckC / Math.Sqrt(s.TypicalN) : double.NaN;
        s.RefInSe = s.SeAtTypicalN > 0 ? s.RefDelta / s.SeAtTypicalN : double.NaN;

        s.CorrValueInclusion = Pearson(r.Cards.Select(c => c.Value), r.Cards.Select(c => c.Inclusion));
        s.CorrValueAvgC = Pearson(r.Cards.Select(c => c.Value), r.Cards.Select(c => c.AvgC));
        s.CorrPopularityAvgC = Pearson(r.Cards.Select(c => c.Popularity), r.Cards.Select(c => c.AvgC));
    }

    public static double Pearson(IEnumerable<double> xs, IEnumerable<double> ys)
    {
        var x = xs.ToList(); var y = ys.ToList();
        if (x.Count < 2) return double.NaN;
        double mx = x.Average(), my = y.Average();
        double sxy = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum();
        double sxx = x.Sum(a => (a - mx) * (a - mx)), syy = y.Sum(b => (b - my) * (b - my));
        return sxx == 0 || syy == 0 ? double.NaN : sxy / Math.Sqrt(sxx * syy);
    }
}
