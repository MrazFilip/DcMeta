// Runs the Master-Prompt analysis and writes:
//   recommended_99.txt       the optimal list, one "1 Card Name" per line (paste into any deck site)
//   report.md                full markdown report, every table printed
//   analysis.xlsx            12 required sheets + extras
//   card_roles_template.csv  fill in to enable interaction/velocity densities (if no roles file given)
//
// One step (scrape + analyze) from an MTGTop8 archetype URL:
//   dotnet run --project src/DcMeta.Analyzer -- --url "https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop"
// Or analyze CSVs scraped earlier:
//   dotnet run --project src/DcMeta.Analyzer -- --data data --out out
//
// Options (all optional):
//   --out out                  output folder (default "out"; scraped CSVs go to out/data)
//   --pages N                  max listing pages to scrape with --url (default 50; stops at the last page)
//   --name "My Archetype"      title for the report (default: most common deck name)
//   --roles card_roles.csv     card,role  (role: interaction | velocity; several allowed with ';')
//   --weights table|log        placement weight mode (default table)
//   --include-leagues          keep MTGO league decks (their rank is a record, not a placement)
//   --strict-99                drop decks whose mainboard is not 99
//   --min-players N            drop events smaller than N
//   --creature-threshold N     fixed split instead of automatic 2-means
//   --floor N                  stability floor for the Recommended 99 (default 15)
//   --beta X                   Avg_C refinement weight in the selection score (default 0.25)
//   --shrink K                 shrinkage pseudo-count for AvgC_shrunk (default 5)
//   --high-field N             high-field recalculation threshold (default 128)
//   --cross-delta X            |ΔAvg_C| to flag between archetypes (default 0.5 × SD of C)

using System.Globalization;
using System.Text;
using DcMeta.Analyzer;
using DcMeta.Core;
using DcMeta.Core.Analysis;

var opt = Cli.Parse(args);
string outDir = opt.GetValueOrDefault("out", "out");
string dataDir = opt.GetValueOrDefault("data", ".");
Directory.CreateDirectory(outDir);

if (opt.TryGetValue("url", out var url))
{
    dataDir = Path.Combine(outDir, "data");
    try
    {
        DcMeta.Scraper.ScrapeJob.Run(url, dataDir, pages: int.Parse(opt.GetValueOrDefault("pages", "50")));
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

string decksPath = Path.Combine(dataDir, "decks.csv"), cardsPath = Path.Combine(dataDir, "cards_long.csv");
foreach (var p in new[] { decksPath, cardsPath })
    if (!File.Exists(p)) { Console.Error.WriteLine($"Missing input: {Path.GetFullPath(p)}. Run DcMeta.Scraper first."); return 1; }

var settings = new AnalysisSettings();
double D(string k) => double.Parse(opt[k], CultureInfo.InvariantCulture);
if (opt.TryGetValue("weights", out var wm)) settings.WeightMode = wm.Equals("log", StringComparison.OrdinalIgnoreCase) ? PlacementWeightMode.Log : PlacementWeightMode.Table;
if (opt.ContainsKey("include-leagues")) settings.IncludeLeagues = true;
if (opt.ContainsKey("strict-99")) settings.Strict99 = true;
if (opt.ContainsKey("min-players")) settings.MinFieldSize = (int)D("min-players");
if (opt.ContainsKey("creature-threshold")) settings.CreatureThreshold = (int)D("creature-threshold");
if (opt.ContainsKey("floor")) settings.StabilityFloor = (int)D("floor");
if (opt.ContainsKey("beta")) settings.AvgCRefinement = D("beta");
if (opt.ContainsKey("shrink")) settings.ShrinkageK = D("shrink");
if (opt.ContainsKey("high-field")) settings.HighFieldThreshold = (int)D("high-field");
if (opt.ContainsKey("cross-delta")) settings.CrossDeltaThreshold = D("cross-delta");

var data = CsvIo.Read(decksPath, cardsPath);
Dictionary<string, HashSet<string>>? roles = null;
if (opt.TryGetValue("roles", out var rolesPath))
{
    roles = CsvIo.ReadRoles(rolesPath);
    Console.WriteLine($"Loaded roles for {roles.Count} cards from {rolesPath}");
}

var result = new MetaAnalyzer(settings, roles).Run(data);

// Report title: --name, else the most common deck name in the data.
string name = opt.GetValueOrDefault("name")
    ?? data.Decks.Where(d => d.DeckName.Length > 0).GroupBy(d => d.DeckName, StringComparer.OrdinalIgnoreCase)
           .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
    ?? "Archetype";

string listPath = Path.Combine(outDir, "recommended_99.txt");
DeckListText.Write(listPath, result.Recommended);

File.WriteAllText(Path.Combine(outDir, "report.md"), MarkdownReport.Build(result, name), new UTF8Encoding(false));
XlsxWriter.Write(Path.Combine(outDir, "analysis.xlsx"), WorkbookBuilder.Build(result));

if (roles is null && result.Cards.Count > 0)
{
    using var w = new StreamWriter(Path.Combine(outDir, "card_roles_template.csv"), false, new UTF8Encoding(true));
    w.WriteLine("card,section,decks_present,role");
    foreach (var c in result.Cards.OrderByDescending(c => c.DecksPresent).ThenBy(c => c.Card))
        w.WriteLine($"{CsvIo.Escape(c.Card)},{CsvIo.Escape(c.Section)},{c.DecksPresent},");
}

Console.WriteLine($"Decks: {data.Decks.Count} scraped, {result.Decks.Count} included");
Console.WriteLine($"Cards: {result.Cards.Count} unique; Recommended 99 = {result.Recommended.Sum(x => x.Qty)} cards");
foreach (var a in result.ArchetypeStats)
    Console.WriteLine($"  {a.Archetype,-15} {a.Decks,4} decks  avg C {a.AvgC:0.000}");
foreach (var note in result.RecommendedNotes) Console.WriteLine($"  note: {note}");
Console.WriteLine($"Wrote {Path.GetFullPath(listPath)}");
Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, "report.md"))}");
Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, "analysis.xlsx"))}");
return 0;
