using System.Globalization;
using System.Text;
using DcMeta.Core.Analysis;

namespace DcMeta.Analyzer;

/// <summary>
/// Writes the full markdown report the Master Prompt asks for: every table printed in full,
/// interpretation text computed from the actual numbers, correlation kept separate from deck theory.
/// </summary>
public static class MarkdownReport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(AnalysisResult r, string archetypeName)
    {
        var md = new StringBuilder();
        var s = r.Summary;
        var set = r.Settings;
        int nAll = r.Data.Decks.Count, n = r.Decks.Count;

        md.AppendLine($"# {archetypeName} — Archetype Dataset Analysis");
        md.AppendLine();
        md.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Source: scraped MTGTop8 decklists only. " +
                      "No external card database, metagame data or prior knowledge was used: card types come from MTGTop8's own deck sections.");
        md.AppendLine();
        md.AppendLine("All metrics below are **correlational**. They describe which cards appear in decks that placed well; " +
                      "they do not measure what a card causes.");
        md.AppendLine();

        md.AppendLine("## 0. Dataset and metadata audit");
        md.AppendLine();
        md.AppendLine($"{nAll} decks scraped, {n} included, {nAll - n} excluded.");
        md.AppendLine();
        Table(md, new[] { "Reason", "Decks" },
            r.Audit.GroupBy(a => a.Included ? "included" : a.Reason).OrderByDescending(g => g.Count())
                   .Select(g => new object?[] { g.Key, g.Count() }));
        var flagged = r.Audit.Where(a => a.Deck.Flags.Length > 0).ToList();
        if (flagged.Count > 0)
        {
            md.AppendLine("Decks carrying data-quality flags:");
            md.AppendLine();
            Table(md, new[] { "Deck", "Player", "Event", "Flags", "Included" },
                flagged.Select(a => new object?[] { a.Deck.DeckId, a.Deck.Player, a.Deck.Event, a.Deck.Flags, a.Included ? "yes" : "no" }));
        }
        if (n == 0)
        {
            md.AppendLine("**No decks passed the filters; nothing further to analyse.**");
            return md.ToString();
        }

        md.AppendLine("## 1. Tournament Coefficient (C)");
        md.AppendLine();
        md.AppendLine("**Definition.** `C = ln(Field Size) × Placement Weight`.");
        md.AppendLine();
        if (set.WeightMode == PlacementWeightMode.Table)
        {
            md.AppendLine("Placement Weight is taken from the placement band (MTGTop8 reports ranges such as 3-4 or 5-8; the band midpoint is used):");
            md.AppendLine();
            double prev = 0;
            Table(md, new[] { "Placement midpoint", "Weight" }, set.PlacementTable.Select(b =>
            {
                string label = b.MaxMid == double.MaxValue ? $"> {F(prev, 1)}" : $"{F(prev, 1)} < mid ≤ {F(b.MaxMid, 1)}";
                prev = b.MaxMid;
                return new object?[] { label, b.Weight };
            }));
        }
        else
        {
            md.AppendLine("Placement Weight = `(log2 F − log2 placement_mid + 1) / (log2 F + 1)`, so 1st place = 1.0 and the weight falls with each halving of the remaining field.");
            md.AppendLine();
        }
        md.AppendLine("**Why a logarithm.** Each doubling of the field adds roughly one extra round of Swiss. ln(F) grows by a constant step per doubling, " +
                      "so a 128-player event is worth more than a 16-player one, but not eight times more. A linear field term would let a handful of large events dominate every card metric.");
        md.AppendLine();
        md.AppendLine("**What C measures.** C is a performance weight for a single list: how deep it went, scaled by how hard the event was to go deep in. " +
                      "It is a **correlation metric, not a causal one** — a high C says the list did well, not why. Pilot skill, matchups and variance are all inside it.");
        md.AppendLine();
        md.AppendLine($"Deck C: mean {F(s.MeanDeckC)}, SD {F(s.SdDeckC)}, min {F(s.MinDeckC)}, max {F(s.MaxDeckC)}.");
        md.AppendLine();
        bool recency = set.RecencyHalfLifeDays is not null;
        Table(md, new[] { "Deck", "Player", "Event", "Date", "Players", "ln(F)", "Rank", "Mid", "PW", "C", "Archetype" }
                  .Concat(recency ? new[] { "Recency", "C×Recency" } : Array.Empty<string>()).ToArray(),
            r.Decks.OrderByDescending(d => d.C).Select(d => new object?[]
            {
                d.Deck.DeckId, d.Deck.Player, d.Deck.Event, d.Deck.Date, d.Deck.FieldSize, d.LnField, d.Deck.RankRaw,
                d.Deck.PlacementMid, d.PlacementWeight, d.C, d.Archetype,
            }.Concat(recency ? new object?[] { d.Recency, d.W } : Array.Empty<object?>()).ToArray()));

        md.AppendLine("## 2. Archetype identification");
        md.AppendLine();
        md.AppendLine($"**Method.** {r.ClusteringMethod}");
        md.AppendLine();
        md.AppendLine("Signals per deck: creature density = creatures / mainboard; spell density = (instants + sorceries + other spells) / mainboard. " +
                      (r.HasRoles
                          ? "Interaction and velocity densities come from the supplied card_roles.csv."
                          : "Interaction and velocity densities need a role for each card, which MTGTop8 does not provide; supply `card_roles.csv` (a template is written next to the report) to add them."));
        md.AppendLine();
        md.AppendLine("Stability metrics: **silhouette** (−1…1; above 0.5 = well separated, near 0 = on the boundary) and **borderline %** " +
                      $"(decks within ±{set.BorderlineBand} creatures of the split, or silhouette < 0.1 when clustering on roles).");
        md.AppendLine();
        Table(md, new[] { "Archetype", "Decks", "Share %", "Avg C", "SD C", "Median C", "Mean creatures", "SD creatures", "Min", "Max", "Spell dens.", "Interaction dens.", "Velocity dens.", "Silhouette", "Borderline %" },
            r.ArchetypeStats.Select(a => new object?[]
            {
                a.Archetype, a.Decks, a.SharePct, a.AvgC, a.SdC, a.MedianC, a.MeanCreatures, a.SdCreatures, a.MinCreatures, a.MaxCreatures,
                a.MeanSpellDensity, a.MeanInteractionDensity, a.MeanVelocityDensity, a.MeanSilhouette, a.BorderlinePct,
            }));
        md.AppendLine("Assignment per deck:");
        md.AppendLine();
        Table(md, new[] { "Deck", "Player", "Event", "Creatures", "Inst/Sorc", "Other", "Lands", "Creature dens.", "Spell dens.", "Archetype", "Silhouette", "Borderline", "C" },
            r.Decks.OrderBy(d => d.Archetype).ThenByDescending(d => d.CreatureDensity).Select(d => new object?[]
            {
                d.Deck.DeckId, d.Deck.Player, d.Deck.Event, d.Deck.NCreatures, d.Deck.NInstantSorcery, d.Deck.NOtherSpells, d.Deck.NLands,
                d.CreatureDensity, d.SpellDensity, d.Archetype, d.Silhouette, d.Borderline ? "yes" : "", d.C,
            }));

        md.AppendLine("## 3. Card Value (primary metric)");
        md.AppendLine();
        md.AppendLine("Definitions (a card is *present* in a deck if it has ≥ 1 copy):");
        md.AppendLine();
        md.AppendLine("- `Inclusion = Σ C` over decks containing the card");
        md.AppendLine("- `Density = Inclusion / Σ C (all decks)` — the card's share of total performance weight");
        md.AppendLine("- `Popularity = #decks_present / total decks`");
        md.AppendLine("- `Inclusion_norm = Inclusion / max(Inclusion)`, `Density_norm = Density / max(Density)`");
        md.AppendLine($"- `Value = {F(set.InclusionWeight, 2)}·Inclusion_norm + {F(set.DensityWeight, 2)}·Density_norm` (weights normalized to sum 1)");
        md.AppendLine();
        md.AppendLine($"**Why Value tracks Inclusion.** Density is Inclusion divided by a constant (total C), so after normalization `Density_norm = Inclusion_norm` exactly. " +
                      $"With the prompt's definitions Value is therefore Inclusion rescaled to 0–1 (corr(Value, Inclusion) = {F(s.CorrValueInclusion)}). " +
                      "Value is a measure of **meta centrality**: how much of the dataset's total performance weight a card is attached to. Changing the weights does not change the ranking.");
        md.AppendLine();
        md.AppendLine("**Why ubiquitous cards regress toward the mean.** Inclusion = #decks_present × Avg_C. A card in nearly every deck sums C over nearly every deck, " +
                      $"so its Avg_C converges to the dataset mean ({F(s.MeanDeckC)}). It earns top Value through count, not through concentration in strong decks.");
        md.AppendLine();
        if (set.RecencyHalfLifeDays is double hl)
        {
            var w = r.Decks.Select(d => d.Recency).ToList();
            md.AppendLine($"**Recency weighting is on** (half-life {F(hl, 0)} days). In Inclusion, Density and Value every deck's C is multiplied by " +
                          $"`0.5^(days before the newest deck / {F(hl, 0)})`, so recent results count more. Deck weights range from {F(w.Min(), 2)} (oldest) to {F(w.Max(), 2)}. " +
                          "Avg_C, the archetype stats and the cross-archetype comparison use plain C.");
            md.AppendLine();
        }
        if (set.AvailabilityWindows) NewCardsSection(md, r);
        CardTable(md, r.Cards, "### CardValueFull (all included decks)");
        CardTable(md, r.CardsCreatureBased, $"### CardValueFull_Creaturebased ({r.Decks.Count(d => d.Archetype == Archetypes.CreatureBased)} decks)");
        CardTable(md, r.CardsCreatureLight, $"### CardValueFull_Creaturelight ({r.Decks.Count(d => d.Archetype == Archetypes.CreatureLight)} decks)");

        md.AppendLine("## 4. Avg_C_when_present (hidden-gem lens)");
        md.AppendLine();
        md.AppendLine("`Avg_C_when_present = Inclusion / #decks_present = E[C | card present]`.");
        md.AppendLine();
        md.AppendLine("It measures performance concentration: whether the card tends to sit in the stronger lists. It does **not** measure intrinsic card power, the marginal effect of adding the card, or causation.");
        md.AppendLine();
        md.AppendLine($"Extra columns: `AvgC_shrunk = (n·Avg_C + k·mean C) / (n + k)` with k = {F(set.ShrinkageK, 1)} (pulls small samples toward the dataset mean); `AvgC_z` = z-score of Avg_C across cards.");
        md.AppendLine();
        Table(md, new[] { "Card", "Inclusion", "#decks", "Avg_C", "Popularity", "Density", "Incl_norm", "Dens_norm", "Value", "AvgC_shrunk", "AvgC_z", "Top-C decks" },
            r.Cards.OrderByDescending(c => c.AvgC).ThenByDescending(c => c.DecksPresent).Select(c => new object?[]
            {
                c.Card, c.Inclusion, c.DecksPresent, c.AvgC, c.Popularity, c.Density, c.InclusionNorm, c.DensityNorm, c.Value, c.AvgCShrunk, c.AvgCZ, c.TopExamples,
            }));

        md.AppendLine("## 5. Value vs Avg_C_when_present");
        md.AppendLine();
        md.AppendLine($"corr(Value, Avg_C) = {F(s.CorrValueAvgC)}; corr(Popularity, Avg_C) = {F(s.CorrPopularityAvgC)}.");
        md.AppendLine();
        md.AppendLine("- **Value ≈ Inclusion ≈ meta centrality.** It rewards being in many decks, weighted by how well they did.");
        md.AppendLine("- **Avg_C detects concentration.** It ignores how many decks play a card and asks only how strong those decks were.");
        md.AppendLine("- **Glue cards score lower on Avg_C.** Cantrips, fetchlands and cheap artifacts sit in almost every list, strong or weak, so their Avg_C sits near the mean by construction.");
        md.AppendLine("- **Narrow cards score higher (or lower) on Avg_C.** A card in a few lists inherits those lists' C directly; with small n, one deep run moves it a lot. Read extremes together with #decks_present and AvgC_shrunk.");
        md.AppendLine();
        var stable = r.Cards.Where(c => c.DecksPresent >= r.CardFloor(c, r.EffectiveFloor)).ToList();
        AvgCRankTable(md, $"### Top 15 by Avg_C — all cards", r.Cards.OrderByDescending(c => c.AvgC).ThenByDescending(c => c.DecksPresent).Take(15));
        AvgCRankTable(md, $"### Bottom 15 by Avg_C — all cards", r.Cards.OrderBy(c => c.AvgC).ThenByDescending(c => c.DecksPresent).Take(15));
        AvgCRankTable(md, $"### Top 15 by Avg_C — stable cards (#decks ≥ {r.EffectiveFloor})", stable.OrderByDescending(c => c.AvgC).Take(15));
        AvgCRankTable(md, $"### Bottom 15 by Avg_C — stable cards (#decks ≥ {r.EffectiveFloor})", stable.OrderBy(c => c.AvgC).Take(15));

        md.AppendLine("### Inside the Recommended 99 only");
        md.AppendLine();
        var recCards = r.Recommended.Select(x => r.Cards.First(c => c.Card == x.Card)).ToList();
        var valueRank = recCards.OrderByDescending(c => c.Value).Select((c, i) => (c.Card, i + 1)).ToDictionary(x => x.Card, x => x.Item2);
        var avgRank = recCards.OrderByDescending(c => c.AvgC).Select((c, i) => (c.Card, i + 1)).ToDictionary(x => x.Card, x => x.Item2);
        md.AppendLine($"Rank shift = Value rank − Avg_C rank. Positive = the card ranks higher on concentration than on centrality. " +
                      $"corr(Value, Avg_C) within the 99 = {F(MetaAnalyzer.Pearson(recCards.Select(c => c.Value), recCards.Select(c => c.AvgC)))}.");
        md.AppendLine();
        Table(md, new[] { "Card", "Section", "Value", "Value rank", "Avg_C", "Avg_C rank", "Rank shift", "#decks" },
            recCards.OrderByDescending(c => c.AvgC).Select(c => new object?[]
            {
                c.Card, c.Section, c.Value, valueRank[c.Card], c.AvgC, avgRank[c.Card], valueRank[c.Card] - avgRank[c.Card], c.DecksPresent,
            }));

        md.AppendLine("## 6. Recommended 99");
        md.AppendLine();
        md.AppendLine("**Construction.**");
        md.AppendLine();
        md.AppendLine($"1. Section quotas = median per-deck count of each MTGTop8 section, rebalanced to 99: " +
                      string.Join(", ", r.SectionQuotas.Select(kv => $"{kv.Key} {kv.Value}")) + ". This keeps the dataset's typical structure.");
        md.AppendLine($"2. Eligibility: #decks_present ≥ {r.EffectiveFloor} (stability floor; requested {set.StabilityFloor})." +
                      (set.AvailabilityWindows
                          ? $" New cards need the floor scaled to their window (floor × window decks / all decks), but at least {set.WindowMinDecks} decks."
                          : ""));
        md.AppendLine($"3. Ranking: `score = {F(1 - set.AvgCRefinement, 2)}·Value + {F(set.AvgCRefinement, 2)}·AvgC_shrunk_norm` — Value is primary; shrunk Avg_C refines the order without letting small samples jump the queue.");
        md.AppendLine("4. Basic lands take their median copy count. If a section cannot be filled at the floor, the floor is lowered one deck at a time for that section only and the card is marked.");
        md.AppendLine();
        foreach (var note in r.RecommendedNotes) md.AppendLine($"- {note}");
        if (r.RecommendedNotes.Count > 0) md.AppendLine();
        Table(md, new[] { "Card", "Category", "Qty", "Value", "Avg_C", "AvgC_shrunk", "#decks", "Score", "Reason_for_Inclusion" },
            r.Recommended.Select(x => new object?[] { x.Card, x.Category, x.Qty, x.Value, x.AvgC, x.AvgCShrunk, x.DecksPresent, x.Score, x.Reason }));
        md.AppendLine($"Total cards: **{r.Recommended.Sum(x => x.Qty)}**.");
        md.AppendLine();
        md.AppendLine("Structural check (Recommended 99 vs dataset):");
        md.AppendLine();
        Table(md, new[] { "Metric", "Dataset median", "Dataset mean", "Recommended 99" },
            r.Structure.Select(x => new object?[] { x.Metric, x.DatasetMedian, x.DatasetMean, x.Recommended }));

        md.AppendLine("## 7. Cut list");
        md.AppendLine();
        md.AppendLine($"Cards played in ≥ {set.CutListMinDecks} decks that did not make the 99, sorted by selection score. " +
                      "*Watch* marks cards whose Avg_C is high but whose sample is too small to trust.");
        md.AppendLine();
        Table(md, new[] { "Card", "Category", "Verdict", "Value", "Avg_C", "#decks", "Score", "Reason" },
            r.CutList.Select(c => new object?[] { c.Card, c.Category, c.Verdict, c.Value, c.AvgC, c.DecksPresent, c.Score, c.Reason }));

        md.AppendLine("## 8. Cross-archetype analysis");
        md.AppendLine();
        md.AppendLine($"Avg_C is recomputed inside each archetype. A difference is flagged when both sides have ≥ {set.CrossMinDecksPerSide} decks and |Δ| ≥ {F(r.CrossThresholdUsed)} " +
                      $"({(set.CrossDeltaThreshold is null ? "0.5 × SD of deck C" : "manual threshold")}). Welch's t is shown as a rough guide to how far Δ sits from noise; with small groups treat it as descriptive.");
        md.AppendLine();
        var sig = r.Cross.Where(x => x.Significant).ToList();
        md.AppendLine($"### Flagged cards ({sig.Count})");
        md.AppendLine();
        CrossTable(md, sig);
        md.AppendLine("### All cards");
        md.AppendLine();
        CrossTable(md, r.Cross);

        md.AppendLine("## 9. Statistical interpretation");
        md.AppendLine();
        md.AppendLine($"**Spread.** Across all {r.Cards.Count} cards, Avg_C runs from {F(s.MinAvgC)} to {F(s.MaxAvgC)} (range {F(s.RangeAvgC)}, SD {F(s.SdAvgC)}). " +
                      (s.StableCards > 0
                          ? $"Across the {s.StableCards} cards with #decks ≥ {r.EffectiveFloor}, it runs from {F(s.MinAvgCStable)} to {F(s.MaxAvgCStable)} (range {F(s.RangeAvgCStable)}, SD {F(s.SdAvgCStable)}). "
                          : "") +
                      ExtremesNote(r));
        md.AppendLine();
        md.AppendLine($"**What a difference of {F(s.RefDelta, 1)} means.** Two cards whose Avg_C differ by {F(s.RefDelta, 1)} sit, on average, in decks whose C differs by {F(s.RefDelta, 1)}. That is:");
        md.AppendLine();
        md.AppendLine($"- {F(s.RefPctOfRange, 0)}% of the full Avg_C range" + (s.StableCards > 0 ? $" and {F(s.RefPctOfRangeStable, 0)}% of the stable-card range;" : ";"));
        md.AppendLine($"- {F(s.RefInDeckSd, 2)} standard deviations of deck C;");
        md.AppendLine($"- {F(s.RefInSe, 1)} standard errors for a card seen in {F(s.TypicalN, 0)} decks (SE = SD(C)/√n = {F(s.SeAtTypicalN)}).");
        md.AppendLine();
        string size = double.IsNaN(s.RefInSe) ? "cannot be judged with this sample"
            : s.RefInSe >= 2 ? "is larger than sampling noise would usually produce at that sample size"
            : "is within the range sampling noise can produce at that sample size";
        md.AppendLine($"For a typical Recommended-99 card, a {F(s.RefDelta, 1)} gap {size}. For cards with fewer decks the SE grows with 1/√n, so the same gap means less.");
        md.AppendLine();
        md.AppendLine("**Conditional expectation vs marginal effect.** Avg_C is E[C | card present]. It is *not* E[C | add card] − E[C | omit card]. " +
                      "Cards travel in packages (the same players, the same build), so a card's Avg_C largely reflects the decks and pilots it appears with. " +
                      "Estimating a marginal effect would need controlled comparisons (same pilot, one-card swaps) that this dataset does not contain.");
        md.AppendLine();

        md.AppendLine("## 10. Advanced");
        md.AppendLine();
        md.AppendLine("- **Z-score of Avg_C** — `AvgC_z` column in sections 3–4.");
        md.AppendLine($"- **Stability-adjusted Avg_C** — `AvgC_shrunk` (k = {F(set.ShrinkageK, 1)}); this is what the Recommended 99 uses.");
        md.AppendLine();
        md.AppendLine($"### High-field recalculation (events with ≥ {set.HighFieldThreshold} players)");
        md.AppendLine();
        if (r.HighFieldDecks < 5)
            md.AppendLine($"Only {r.HighFieldDecks} decks come from events of that size, which is too few to recalculate meaningfully. Lower `--high-field` to use this section.");
        else
            CardTable(md, r.CardsHighField, $"{r.HighFieldDecks} decks.");
        md.AppendLine();

        md.AppendLine("## Appendix — settings used");
        md.AppendLine();
        Table(md, new[] { "Setting", "Value" }, WorkbookBuilder.SettingsRows(r).Select(x => new object?[] { x.Item1, x.Item2 }));
        return md.ToString();
    }

    static string ExtremesNote(AnalysisResult r)
    {
        var ext = r.Cards.OrderByDescending(c => c.AvgC).Take(10)
            .Concat(r.Cards.OrderBy(c => c.AvgC).Take(10)).ToList();
        int small = ext.Count(c => c.DecksPresent < r.CardFloor(c, r.EffectiveFloor));
        return small == 0
            ? "All of the 10 highest and 10 lowest Avg_C cards meet the stability floor, so the extremes are not small-sample artefacts."
            : $"{small} of the 10 highest and 10 lowest Avg_C cards are below the stability floor ({r.EffectiveFloor} decks); those extremes mostly reflect small samples.";
    }

    static void NewCardsSection(StringBuilder md, AnalysisResult r)
    {
        var set = r.Settings;
        md.AppendLine("### Availability windows (new cards)");
        md.AppendLine();
        md.AppendLine("A card printed recently could not appear in older decks, so it is measured only against decks dated on or after its release " +
                      "(earliest printing on Scryfall; if unknown, the first date it appears in this data). For those cards:");
        md.AppendLine();
        md.AppendLine("- `Popularity = #decks_present / window decks`");
        md.AppendLine($"- `Density = (Inclusion + k·mean C·Density_all) / (Σ C in window + k·mean C)` with k = {F(set.WindowShrinkK, 1)} — the window's density, pulled toward the whole-dataset density so a short window is trusted less");
        md.AppendLine("- `Inclusion_norm` uses window-projected inclusion (Density × Σ C), so with windows on `Inclusion_norm = Density_norm` and Value reflects the adjusted density. Raw `Inclusion` is still shown unadjusted.");
        md.AppendLine();
        var newCards = r.Cards.Where(c => c.IsWindowed).OrderByDescending(c => c.Value).ToList();
        if (newCards.Count == 0) { md.AppendLine("_No card is newer than the oldest deck._"); md.AppendLine(); return; }
        Table(md, new[] { "Card", "Section", "Available from", "Source", "#decks", "Window decks", "Popularity", "Density", "Value", "Floor needed", "In 99" },
            newCards.Select(c => new object?[]
            {
                c.Card, c.Section, c.AvailableFrom?.ToString("yyyy-MM-dd"), c.AvailableSource, c.DecksPresent, c.WindowDecks,
                c.Popularity, c.Density, c.Value, r.CardFloor(c, r.EffectiveFloor), r.Recommended.Any(x => x.Card == c.Card) ? "yes" : "",
            }));
    }

    static void CardTable(StringBuilder md, List<CardMetric> cards, string title)
    {
        md.AppendLine(title);
        md.AppendLine();
        if (cards.Count == 0) { md.AppendLine("_No decks in this group._"); md.AppendLine(); return; }
        Table(md, new[] { "Card", "Section", "Inclusion", "Density", "Popularity", "#decks", "Incl_norm", "Dens_norm", "Value", "Avg_C", "AvgC_shrunk", "AvgC_z" },
            cards.Select(c => new object?[] { c.Card, c.Section, c.Inclusion, c.Density, c.Popularity, c.DecksPresent, c.InclusionNorm, c.DensityNorm, c.Value, c.AvgC, c.AvgCShrunk, c.AvgCZ }));
    }

    static void AvgCRankTable(StringBuilder md, string title, IEnumerable<CardMetric> cards)
    {
        md.AppendLine(title);
        md.AppendLine();
        var list = cards.ToList();
        if (list.Count == 0) { md.AppendLine("_No cards meet this condition._"); md.AppendLine(); return; }
        Table(md, new[] { "Card", "Section", "Avg_C", "AvgC_shrunk", "#decks", "Popularity", "Value" },
            list.Select(c => new object?[] { c.Card, c.Section, c.AvgC, c.AvgCShrunk, c.DecksPresent, c.Popularity, c.Value }));
    }

    static void CrossTable(StringBuilder md, List<CrossRow> rows)
    {
        if (rows.Count == 0) { md.AppendLine("_None._"); md.AppendLine(); return; }
        Table(md, new[] { "Card", "Section", "#CB", "Avg_C CB", "#CL", "Avg_C CL", "Δ (CB−CL)", "Welch t", "Flag", "Note" },
            rows.Select(x => new object?[] { x.Card, x.Section, x.NCreatureBased, x.AvgCCreatureBased, x.NCreatureLight, x.AvgCCreatureLight, x.Delta, x.WelchT, x.Significant ? "yes" : "", x.Note }));
    }

    static void Table(StringBuilder md, string[] headers, IEnumerable<object?[]> rows)
    {
        md.Append("| ").Append(string.Join(" | ", headers.Select(Cell))).AppendLine(" |");
        md.Append("|").Append(string.Join("|", headers.Select(_ => "---"))).AppendLine("|");
        foreach (var row in rows)
            md.Append("| ").Append(string.Join(" | ", row.Select(v => Cell(Fmt(v))))).AppendLine(" |");
        md.AppendLine();
    }

    static string Fmt(object? v) => v switch
    {
        null => "",
        double d when double.IsNaN(d) => "n/a",
        double d => d.ToString("0.000", Inv),
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    static string F(double v, int decimals = 3) =>
        double.IsNaN(v) || double.IsInfinity(v) ? "n/a" : v.ToString("F" + decimals, Inv);
}
