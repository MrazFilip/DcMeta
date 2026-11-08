using DcMeta.Core;
using DcMeta.Core.Analysis;

namespace DcMeta.Analyzer;

/// <summary>Maps an <see cref="AnalysisResult"/> onto the workbook sheets the Master Prompt requires.</summary>
public static class WorkbookBuilder
{
    static double? N(double v) => double.IsNaN(v) ? null : v;

    public static List<Sheet> Build(AnalysisResult r)
    {
        var sheets = new List<Sheet>();
        var scored = r.Decks.ToDictionary(d => d.Deck.DeckId);

        var raw = new Sheet("Raw_Parsed_Decks", CsvIo.DeckHeader.Concat(new[] { "cards_listed" }).ToArray());
        var cardsByDeck = r.Data.Cards.ToLookup(c => c.DeckId);
        foreach (var d in r.Data.Decks)
            raw.Add(d.DeckId, d.EventId, d.DeckName, d.Player, d.Event, d.Level, d.RankRaw, d.Date, d.FieldSize, d.Record,
                    d.PlacementBest, d.PlacementWorst, d.PlacementMid, d.Commander, d.NMainboard, d.NLands, d.NCreatures,
                    d.NInstantSorcery, d.NOtherSpells, d.NUnknownType, d.DeckUrl, d.Flags,
                    string.Join("; ", cardsByDeck[d.DeckId].Select(c => $"{c.Qty} {c.Card}")));
        sheets.Add(raw);

        var audit = new Sheet("Metadata_Audit", "deck_id", "player", "event", "date", "field_size", "rank_raw",
                              "n_mainboard", "flags", "included", "reason");
        foreach (var a in r.Audit)
            audit.Add(a.Deck.DeckId, a.Deck.Player, a.Deck.Event, a.Deck.Date, a.Deck.FieldSize, a.Deck.RankRaw,
                      a.Deck.NMainboard, a.Deck.Flags, a.Included, a.Reason);
        sheets.Add(audit);

        var tc = new Sheet("Tournament_Coefficients", "deck_id", "player", "event", "date", "field_size", "ln_field_size",
                           "rank_raw", "placement_mid", "placement_weight", "C", "archetype", "recency_weight", "C_x_recency");
        foreach (var d in r.Decks.OrderByDescending(d => d.C))
            tc.Add(d.Deck.DeckId, d.Deck.Player, d.Deck.Event, d.Deck.Date, d.Deck.FieldSize, d.LnField, d.Deck.RankRaw,
                   d.Deck.PlacementMid, d.PlacementWeight, d.C, d.Archetype, d.Recency, d.W);
        sheets.Add(tc);

        var split = new Sheet("Archetype_Split", "deck_id", "player", "event", "n_creatures", "n_instant_sorcery",
                              "n_other_spells", "n_lands", "creature_density", "spell_density", "interaction_density",
                              "velocity_density", "archetype", "silhouette", "borderline", "C");
        foreach (var d in r.Decks.OrderBy(d => d.Archetype).ThenByDescending(d => d.CreatureDensity))
            split.Add(d.Deck.DeckId, d.Deck.Player, d.Deck.Event, d.Deck.NCreatures, d.Deck.NInstantSorcery,
                      d.Deck.NOtherSpells, d.Deck.NLands, d.CreatureDensity, d.SpellDensity, d.InteractionDensity,
                      d.VelocityDensity, d.Archetype, d.Silhouette, d.Borderline, d.C);
        sheets.Add(split);

        var ac = new Sheet("Archetype_Coefficients", "archetype", "decks", "representation_pct", "avg_C", "sd_C", "median_C",
                           "total_C", "mean_creatures", "sd_creatures", "min_creatures", "max_creatures",
                           "mean_spell_density", "mean_interaction_density", "mean_velocity_density",
                           "mean_silhouette", "borderline_pct");
        foreach (var a in r.ArchetypeStats)
            ac.Add(a.Archetype, a.Decks, a.SharePct, a.AvgC, N(a.SdC), a.MedianC, a.TotalC, a.MeanCreatures, N(a.SdCreatures),
                   a.MinCreatures, a.MaxCreatures, a.MeanSpellDensity, a.MeanInteractionDensity, a.MeanVelocityDensity,
                   a.MeanSilhouette, a.BorderlinePct);
        sheets.Add(ac);

        sheets.Add(CardValueSheet("CardValueFull", r.Cards));
        sheets.Add(CardValueSheet("CardValueFull_Creaturebased", r.CardsCreatureBased));
        sheets.Add(CardValueSheet("CardValueFull_Creaturelight", r.CardsCreatureLight));

        // Mandatory columns first, extras after
        var avg = new Sheet("Card_AvgC_when_present", "Card", "Inclusion", "#decks_present", "Avg_C_when_present",
                            "Popularity", "Density", "Inclusion_norm", "Density_norm", "Value", "decks_topC_examples",
                            "Section", "AvgC_shrunk", "AvgC_z");
        foreach (var c in r.Cards.OrderByDescending(c => c.AvgC).ThenByDescending(c => c.DecksPresent))
            avg.Add(c.Card, c.Inclusion, c.DecksPresent, c.AvgC, c.Popularity, c.Density, c.InclusionNorm, c.DensityNorm,
                    c.Value, c.TopExamples, c.Section, c.AvgCShrunk, c.AvgCZ);
        sheets.Add(avg);

        var rec = new Sheet("Recommended_99", "Card", "Category", "Qty", "Value", "Avg_C_when_present", "AvgC_shrunk",
                            "#decks_present", "Selection_score", "Floor_used", "Reason_for_Inclusion");
        foreach (var x in r.Recommended)
            rec.Add(x.Card, x.Category, x.Qty, x.Value, x.AvgC, x.AvgCShrunk, x.DecksPresent, x.Score, x.FloorUsed, x.Reason);
        rec.Add("TOTAL", "", r.Recommended.Sum(x => x.Qty), null, null, null, null, null, null, "");
        sheets.Add(rec);

        var cut = new Sheet("Cut_List", "Card", "Category", "Verdict", "Value", "Avg_C_when_present", "#decks_present",
                            "Selection_score", "Reason");
        foreach (var c in r.CutList)
            cut.Add(c.Card, c.Category, c.Verdict, c.Value, c.AvgC, c.DecksPresent, c.Score, c.Reason);
        sheets.Add(cut);

        var cross = new Sheet("Cross_Archetype_Comparison", "Card", "Section", "#decks_creature_based",
                              "AvgC_creature_based", "#decks_creature_light", "AvgC_creature_light",
                              "Delta_CB_minus_CL", "Welch_t", "Significant", "Note");
        foreach (var x in r.Cross)
            cross.Add(x.Card, x.Section, x.NCreatureBased, x.AvgCCreatureBased, x.NCreatureLight, x.AvgCCreatureLight,
                      x.Delta, x.WelchT, x.Significant, x.Note);
        sheets.Add(cross);

        // Extras beyond the 12 required sheets
        var st = new Sheet("Structural_Check", "Metric", "Dataset_median", "Dataset_mean", "Recommended_99");
        foreach (var s in r.Structure) st.Add(s.Metric, s.DatasetMedian, s.DatasetMean, s.Recommended);
        sheets.Add(st);

        var sum = new Sheet("Statistical_Summary", "Statistic", "Value");
        foreach (var (k, v) in SummaryRows(r)) sum.Add(k, v);
        sheets.Add(sum);

        sheets.Add(CardValueSheet("Advanced_HighField", r.CardsHighField));

        var raws = new Sheet("Raw_Cards_Long", "deck_id", "qty", "card", "section");
        foreach (var c in r.Data.Cards) raws.Add(c.DeckId, c.Qty, c.Card, c.Section);
        sheets.Add(raws);

        var set = new Sheet("Settings_Used", "Setting", "Value");
        foreach (var (k, v) in SettingsRows(r)) set.Add(k, v);
        sheets.Add(set);

        return sheets;
    }

    static Sheet CardValueSheet(string name, List<CardMetric> cards)
    {
        var s = new Sheet(name, "Card", "Section", "Inclusion", "Density", "Popularity", "#decks_present",
                          "Inclusion_norm", "Density_norm", "Value", "Avg_C_when_present", "AvgC_shrunk", "AvgC_z",
                          "Median_qty", "decks_topC_examples", "Window_decks", "Available_from", "Available_source");
        foreach (var c in cards)
            s.Add(c.Card, c.Section, c.Inclusion, c.Density, c.Popularity, c.DecksPresent, c.InclusionNorm, c.DensityNorm,
                  c.Value, c.AvgC, c.AvgCShrunk, c.AvgCZ, c.MedianQty, c.TopExamples,
                  c.WindowDecks, c.AvailableFrom?.ToString("yyyy-MM-dd"), c.AvailableSource);
        return s;
    }

    public static IEnumerable<(string, object?)> SummaryRows(AnalysisResult r)
    {
        var s = r.Summary;
        yield return ("Decks scraped", r.Data.Decks.Count);
        yield return ("Decks included", r.Decks.Count);
        yield return ("Unique cards (included decks)", r.Cards.Count);
        yield return ("Deck C mean", s.MeanDeckC);
        yield return ("Deck C SD", N(s.SdDeckC));
        yield return ("Deck C min", s.MinDeckC);
        yield return ("Deck C max", s.MaxDeckC);
        yield return ("Avg_C min (all cards)", s.MinAvgC);
        yield return ("Avg_C max (all cards)", s.MaxAvgC);
        yield return ("Avg_C range (all cards)", s.RangeAvgC);
        yield return ("Avg_C SD (all cards)", N(s.SdAvgC));
        yield return ($"Cards with #decks ≥ {r.EffectiveFloor}", s.StableCards);
        yield return ("Avg_C min (stable cards)", s.StableCards > 0 ? s.MinAvgCStable : null);
        yield return ("Avg_C max (stable cards)", s.StableCards > 0 ? s.MaxAvgCStable : null);
        yield return ("Avg_C range (stable cards)", s.StableCards > 0 ? s.RangeAvgCStable : null);
        yield return ("Avg_C SD (stable cards)", N(s.SdAvgCStable));
        yield return ("Reference ΔAvg_C", s.RefDelta);
        yield return ("Δ as % of range (all)", N(s.RefPctOfRange));
        yield return ("Δ as % of range (stable)", N(s.RefPctOfRangeStable));
        yield return ("Δ in deck-C SD units", N(s.RefInDeckSd));
        yield return ("Typical #decks_present (Recommended 99 median)", s.TypicalN);
        yield return ("SE of Avg_C at typical n", N(s.SeAtTypicalN));
        yield return ("Δ in SE units at typical n", N(s.RefInSe));
        yield return ("corr(Value, Inclusion)", N(s.CorrValueInclusion));
        yield return ("corr(Value, Avg_C)", N(s.CorrValueAvgC));
        yield return ("corr(Popularity, Avg_C)", N(s.CorrPopularityAvgC));
        yield return ("Creature threshold used", r.CreatureThresholdUsed);
        yield return ("Cross-archetype Δ threshold", r.CrossThresholdUsed);
        yield return ($"High-field decks (≥ {r.Settings.HighFieldThreshold} players)", r.HighFieldDecks);
        if (r.Settings.AvailabilityWindows) yield return ("Cards newer than the oldest deck (windowed)", r.WindowedCards);
    }

    public static IEnumerable<(string, object?)> SettingsRows(AnalysisResult r)
    {
        var s = r.Settings;
        yield return ("Placement weight mode", s.WeightMode.ToString());
        if (s.WeightMode == PlacementWeightMode.Table)
            foreach (var (mid, w) in s.PlacementTable)
                yield return ($"  weight if placement_mid ≤ {(mid == double.MaxValue ? "∞" : mid.ToString(System.Globalization.CultureInfo.InvariantCulture))}", w);
        yield return ("Include MTGO leagues", s.IncludeLeagues);
        yield return ("Min field size", s.MinFieldSize);
        yield return ("Strict 99", s.Strict99);
        yield return ("Manual creature threshold", s.CreatureThreshold?.ToString() ?? "auto (2-means)");
        yield return ("Clustering method", r.ClusteringMethod);
        yield return ("Card roles file used", r.HasRoles);
        yield return ("Inclusion weight", s.InclusionWeight);
        yield return ("Density weight", s.DensityWeight);
        yield return ("Shrinkage k", s.ShrinkageK);
        yield return ("Recency half-life (days)", s.RecencyHalfLifeDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off");
        yield return ("Availability windows (new cards)", s.AvailabilityWindows);
        if (s.AvailabilityWindows)
        {
            yield return ("  window shrinkage k", s.WindowShrinkK);
            yield return ("  min decks for a new card", s.WindowMinDecks);
        }
        yield return ("Stability floor (requested)", s.StabilityFloor);
        yield return ("Stability floor (effective)", r.EffectiveFloor);
        yield return ("Avg_C refinement β", s.AvgCRefinement);
        yield return ("Cut list min decks", s.CutListMinDecks);
        yield return ("Cross min decks per side", s.CrossMinDecksPerSide);
        yield return ("High-field threshold", s.HighFieldThreshold);
    }
}
