// Scrapes one MTGTop8 archetype into decks.csv, cards_long.csv and dataset.txt.
//
//   dotnet run --project src/DcMeta.Scraper -- --url "https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH&color_id=BR&show=pop" --out data
//   options: --pages <n> (max listing pages, default 50; stops early when a page is empty)  --min-players <n>  --delay <seconds>
//
// To scrape AND analyze in one go, use DcMeta.Analyzer with --url instead.

using System.Globalization;
using DcMeta.Core;
using DcMeta.Scraper;

var opt = Cli.Parse(args);
if (!opt.TryGetValue("url", out var url))
{
    Console.Error.WriteLine("Usage: --url \"https://www.mtgtop8.com/archetype?a=...&meta=...&f=EDH\" [--out data] [--pages 50]");
    return 1;
}

try
{
    ScrapeJob.Run(url,
        outDir: opt.GetValueOrDefault("out", "data"),
        pages: int.Parse(opt.GetValueOrDefault("pages", "50")),
        minPlayers: int.Parse(opt.GetValueOrDefault("min-players", "0")),
        delaySeconds: double.Parse(opt.GetValueOrDefault("delay", "1.5"), CultureInfo.InvariantCulture));
    return 0;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
