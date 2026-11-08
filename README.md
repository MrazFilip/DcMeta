# DcMeta

Paste an MTGTop8 archetype URL, get the optimal decklist.

DcMeta scrapes every deck behind the URL, analyzes them, and writes a ready-to-import `.txt` list plus a full report. Built for 99-card commander formats (Duel Commander, `f=EDH`), but works with any archetype on MTGTop8.

The analysis logic (the "Master Analysis Prompt") was created by [Ervin Kuč](https://www.linkedin.com/in/ervin-ku%C4%8D/).

## Requirements

.NET 8 SDK.

## Usage

1. On MTGTop8, open the archetype and set the filters you want (time window, colour identity, sort).
2. Copy the URL from the address bar.
3. Run it (keep the quotes):

```bash
dotnet run --project src/DcMeta.Analyzer -- --url "https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop" --out out
```

You get:

| File | Contents |
|---|---|
| `out/recommended_99.txt` | The optimal list, one `1 Card Name` per line. Paste into Moxfield, Archidekt, MTGGoldfish, etc. (commander not included) |
| `out/report.md` | The full analysis with all tables |
| `out/analysis.xlsx` | The same analysis as a spreadsheet |
| `out/data/` | The raw scraped decks and cards |

## Options

Most runs need none of these.

| Option | Default | What it does |
|---|---|---|
| `--pages N` | 50 | Max listing pages to scrape (20 decks each) |
| `--min-players N` | 2 | Ignore events with fewer players |
| `--delay S` | 1.5 | Seconds between requests. Please keep it polite |
| `--include-leagues` | off | Include MTGO league decks |
| `--strict-99` | off | Drop decks whose mainboard isn't exactly 99 |
| `--floor N` | 15 | Minimum number of decks a card must appear in to be recommended |
| `--name` | auto | Report title |
| `--weights table\|log` | table | How placements are weighted |
| `--roles file.csv` | — | Card roles file (see below) |
| `--new-cards` | off | Fair scoring for newly released cards (see below) |
| `--half-life DAYS` | off | Count recent decks more (see below) |

Advanced tuning (`--beta`, `--shrink`, `--high-field`, `--cross-delta`, `--creature-threshold`, `--window-k`, `--window-min-decks`) and every default live in [`AnalysisSettings.cs`](src/DcMeta.Core/Analysis/AnalysisSettings.cs).

### New cards (optional)

The scraped decks span months, so a card printed last month can only appear in the newest decks. Normally it's scored against all of them and looks unpopular. With `--new-cards`, each card is judged only against decks played after its release:

- Release dates come from [Scryfall](https://scryfall.com) (earliest printing). They're fetched once per card and cached in `%LOCALAPPDATA%\DcMeta\release_dates.csv`, so the first run takes a minute and later runs are instant.
- A card in 5 of the 5 decks since its release scores well, but short windows are trusted less than long ones, so it still ranks below a card in 80 of 100.
- The deck-count floor shrinks with the window (minimum 3 decks).
- Add `--no-scryfall` to skip the lookup and use the first date each card appears in the data instead.

The report gets a "New cards" table showing each new card's window and whether it made the 99.

### Recent results count more (optional)

`--half-life 90` halves a deck's weight for every 90 days it is older than the newest deck, so the list leans toward the current meta: cards people stopped playing drop, cards that are rising climb. It changes Value (what the 99 is built from), not Avg_C. A good starting point is about half the time span of your data. It works well together with `--new-cards`:

```bash
dotnet run --project src/DcMeta.Analyzer -- --url "<archetype url>" --new-cards --half-life 90
```

### Card roles (optional)

By default, decks are grouped into archetypes by creature count. For better grouping, fill the `role` column of `out/card_roles_template.csv` with `interaction`, `velocity`, or `interaction;velocity`, save it as `card_roles.csv`, and rerun with `--roles card_roles.csv`.

### Scrape now, analyze later

```bash
dotnet run --project src/DcMeta.Scraper -- --url "<archetype url>" --out data
```

```bash
dotnet run --project src/DcMeta.Analyzer -- --data data --out out
```

## Troubleshooting

**0 decks or 0 cards found:** MTGTop8 changed its HTML. Update the lines marked `SELECTOR` in `MtgTop8Client.cs`.

## Project layout

```
src/DcMeta.Core          metrics, models, CSV I/O
src/DcMeta.Scraper       MTGTop8 scraper
src/DcMeta.Analyzer      command-line app: scrape + analyze
tests/DcMeta.Core.Tests  unit tests (run with `dotnet test`)
```
