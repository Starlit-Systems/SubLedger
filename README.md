# SubLedger

An accounting ledger for your Final Fantasy XIV Free Company submarines, as a Dalamud plugin.

SubLedger records what every submarine voyage brings back, values it at NPC vendor prices taken from the game's own item data, and keeps the result in a permanent ledger on your computer. Open it in game with `/subledger`.

It only observes. It never clicks, never sends anything to the game and never sends your data anywhere, so it works alongside whatever you use to collect and redeploy your submarines (AutoRetainer, for example).

## Installing

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address, press the **+** button, then **Save and Close**:

   ```text
   https://raw.githubusercontent.com/starlit-systems/DalamudPlugins/main/pluginmaster.json
   ```

3. Type `/xlplugins`, search for **SubLedger** and install it.

That one address lists every Starlit Systems plugin.

## What it does

- **Records every voyage automatically.** When you collect a submarine, SubLedger reads the voyage result the game shows you: which sectors were visited and exactly which items came back. Items bought on the market board or picked up any other way are never counted, because SubLedger does not look at your inventory at all.
- **Never counts a voyage twice.** Not across restarts, not when another plugin collects for you, and not when you open an old voyage log again.
- **Exact gil.** Values are stored to the single gil. Short forms such as `1.05m` are only a display option.
- **Imports your history.** If you have used SubmarineTracker, its past voyages can be copied into the ledger with one click.
- **Per Free Company.** Each Free Company's submarines are tracked separately, with a combined view when you have more than one.
- **Reports for any period.** Preset or custom date ranges, grouped by day, week, month or quarter, with income charts, a breakdown by item, submarine or route, export to CSV or JSON, and a full interactive HTML report that opens in your browser.
- **Brings in your own records.** A hand-kept list such as `Day 1: 125k` or a two-column spreadsheet can be imported. Where a day has both a hand-kept total and recorded voyages, the voyages count and the total is kept on file, so nothing is doubled.
- **Knows what it cost.** Enter what you spent on parts, housing, fuel and repairs, and SubLedger shows your profit, your return on investment and the day the fleet paid for itself.
- **Looks after its data.** A dated backup is made every day, and if the ledger file is ever damaged SubLedger restores the newest healthy backup on its own.

## Status

This is an early version. Recording, the ledger, the SubmarineTracker import, the dashboard, date-range reports with charts, CSV and JSON export, importing hand-kept totals from text and spreadsheet files, and manual corrections are in. Expense and payback tracking and a standalone HTML report are in too. Still to come: merging ledgers kept on several computers.

## Building from source

Requirements: the .NET 10 SDK, and Dalamud's development files (installed automatically by XIVLauncher under `%AppData%\XIVLauncher\addon\Hooks\dev`).

```powershell
dotnet build SubLedger.slnx -c Release
dotnet test SubLedger.slnx -c Release
```

The packaged plugin is written to `SubLedger\bin\Release\SubLedger\latest.zip`.

The code is in three projects:

| Project | What it holds |
|---|---|
| `SubLedger.Core` | The ledger itself: data model, database, duplicate detection, valuation, importers and analytics. It has no dependency on Dalamud or the game, so it can be tested on its own. |
| `SubLedger` | The plugin: reading voyage results from the game, looking up item prices, and the in-game window. |
| `SubLedger.Tests` | Unit tests for `SubLedger.Core`. |

## Releasing

1. Set the new version in `SubLedger/SubLedger.csproj` and add an entry to `SubLedger/Changelog.cs`.
2. Commit and push to `main`, then push a tag of the same version, for example `v0.1.1.0`. The build workflow attaches `SubLedger.zip` to a GitHub release.
3. Set the same `AssemblyVersion` for SubLedger in `pluginmaster.json` in the [DalamudPlugins](https://github.com/starlit-systems/DalamudPlugins) repository, which is the list players' installers read (and, while it is still in use, in the older list in the HonorificCarousel repository).

## Credits and licence

SubLedger is released under the MIT licence; see [LICENSE](LICENSE). It builds on techniques from three other open-source plugins, credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Made by Starlit Systems. FINAL FANTASY XIV © SQUARE ENIX CO., LTD. SubLedger is a fan-made tool and is not affiliated with Square Enix.
