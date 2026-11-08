# DcMeta — MTGTop8 archetype scraper + analyzer

Give it any MTGTop8 archetype URL and it scrapes the decks, runs the full "Master Analysis Prompt" in code, and writes the optimal list as a plain `.txt` you can paste into any deck site. Works for any commander or archetype on MTGTop8; built around 99-card commander formats (Duel Commander, `f=EDH`).

```
DcMeta.sln
├─ src/DcMeta.Core        models, CSV I/O, URL parsing, all metrics (no dependencies)
├─ src/DcMeta.Scraper     MTGTop8 → decks.csv, cards_long.csv, dataset.txt   (HtmlAgilityPack)
├─ src/DcMeta.Analyzer    scrape + analyze → recommended_99.txt, report.md, analysis.xlsx
├─ tests/DcMeta.Core.Tests  xUnit tests for the metrics and URL parsing
└─ samples/synthetic      fake 68-deck dataset to try the analyzer offline
```

Requires the .NET 8 SDK. Open `DcMeta.sln` in Visual Studio or Rider, or use the CLI below.

## Quick start: one command

1. On MTGTop8, open the archetype you want and set the filters (time window, colour identity, sort).
2. Copy the URL from the address bar.
3. Run (keep the quotes, because the URL contains `&`):

```
dotnet run --project src/DcMeta.Analyzer -- --url "https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop" --out out
```

Every filter in the URL is kept. The command writes:

| File | What it is |
|---|---|
| `out/recommended_99.txt` | the optimal list, one `1 Card Name` per line — paste into Moxfield, Archidekt, MTGGoldfish, Cockatrice, MTGO… (commander not included) |
| `out/report.md` | every section of the prompt, all tables printed in full |
| `out/analysis.xlsx` | the 12 required sheets + Structural_Check, Statistical_Summary, Advanced_HighField, Raw_Cards_Long, Settings_Used |
| `out/card_roles_template.csv` | optional: see "Interaction and velocity densities" |
| `out/data/` | the scraped `decks.csv`, `cards_long.csv`, `dataset.txt` |

To try it without scraping: `dotnet run --project src/DcMeta.Analyzer -- --data samples/synthetic --out out`

## Scraping only

```
dotnet run --project src/DcMeta.Scraper -- --url "<archetype url>" --out data
dotnet run --project src/DcMeta.Analyzer -- --data data --out out     # analyze later
```

| Option | Default | Meaning |
|---|---|---|
| `--url` | — | MTGTop8 archetype URL, as copied from the browser |
| `--pages` | 50 | max listing pages (20 decks each); stops automatically at the last page. Also works on the Analyzer with `--url` |
| `--min-players` | 0 | drop events smaller than N |
| `--delay` | 1.5 | seconds between requests (please keep it polite) |

Card types come from MTGTop8's own deck sections (Lands / Creatures / Instants and Sorc. / Other Spells). No outside card database is used.

If a run reports 0 decks or 0 cards, MTGTop8 has changed its HTML. Open a page, press F12, and adjust the lines marked `SELECTOR` in `MtgTop8Client.cs`.

## Analyzer options

| Option | Default | Meaning |
|---|---|---|
| `--name` | most common deck name | report title |
| `--weights table\|log` | table | placement weight: fixed bands, or `(log2 F − log2 mid + 1)/(log2 F + 1)` |
| `--include-leagues` | off | keep MTGO league decks (their "rank" is a record, not a placement) |
| `--strict-99` | off | drop decks whose mainboard ≠ 99 |
| `--min-players N` | 2 | drop small events |
| `--creature-threshold N` | auto | fixed archetype split instead of 2-means |
| `--floor N` | 15 | stability floor for the Recommended 99 (auto-lowered if > half the dataset) |
| `--beta X` | 0.25 | weight of shrunk Avg_C in the selection score (Value keeps 1 − β) |
| `--shrink K` | 5 | pseudo-count for stability-adjusted Avg_C |
| `--high-field N` | 128 | threshold for the high-field recalculation |
| `--cross-delta X` | 0.5 × SD(C) | \|ΔAvg_C\| that flags a cross-archetype difference |
| `--roles file.csv` | — | card roles for interaction/velocity densities |

### Interaction and velocity densities (optional)

The prompt clusters on interaction and velocity density, but MTGTop8 does not say which cards count as which. Fill the `role` column of `card_roles_template.csv` with `interaction`, `velocity` or both (`interaction;velocity`), save it as `card_roles.csv`, and rerun with `--roles card_roles.csv`.

Without a roles file, archetypes are split on creature count alone.

## Choices the prompt leaves open, and the defaults used

| Prompt item | Implementation |
|---|---|
| Placement Weight | 1st 1.00, 2nd 0.85, 3-4 0.70, 5-8 0.55, 9-16 0.40, 17-32 0.30, lower 0.20 (on the placement-range midpoint); `--weights log` for a field-relative alternative |
| log in C | natural log: `C = ln(F) × PW` |
| Inclusion_norm / Density_norm | divided by their maximum. Under the prompt's definitions the two are identical, so Value = Inclusion_norm; the report says so explicitly |
| Archetype clustering | exact 2-means on creature count; 4-signal k-means when roles are supplied |
| Stability metrics | silhouette and borderline % per archetype |
| Recommended 99 | section quotas = median per-deck counts (rebalanced to 99); eligibility = #decks ≥ floor; score = 0.75·Value + 0.25·shrunk Avg_C (min–max scaled); basics take their median copy count |
| Cut list | non-selected cards in ≥ 2 decks, labelled structural redundancy, low Value + low Avg_C, low support, or "Watch" (high Avg_C, small sample) |
| Cross-archetype | per-archetype Avg_C, Δ, Welch t; flagged when both sides have ≥ 3 decks and \|Δ\| ≥ threshold |

All of these live in `src/DcMeta.Core/Analysis/AnalysisSettings.cs`.

## Tests

```
dotnet test
```
