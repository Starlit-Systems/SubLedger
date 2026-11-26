# Third-party notices

SubLedger's code is its own, but it could not have been written without the work below. Where a technique was learned from another project, that project is named here and at the relevant place in the source.

## SubmarineTracker

<https://github.com/Infiziert90/SubmarineTracker> · Copyright (c) 2023 Infi · MIT License

- Where the game keeps a finished voyage's results (the workshop's submersible data, and the entry for the submarine whose menu is open), and the id of the server message that announces a voyage result. Used in `SubLedger/Game/WorkshopReader.cs` and `SubLedger/Game/VoyageCaptureService.cs`.
- The fact that Island Sanctuary fills the same workshop structure and has to be excluded.
- The layout of SubmarineTracker's own database, which `SubLedger.Core/Import/SubmarineTrackerImporter.cs` reads in order to import a player's history.

## BunnySubSync

<https://github.com/Juusohel/BunnySubSync> · Copyright (c) 2026 Juusohel · MIT License

- The observation that the game clears a submarine's return time just before the voyage result arrives, and the answer to it: remember the last return time seen while the submarine was still out. SubLedger's version of this is in `SubLedger.Core/LedgerStore.cs` and `SubLedger/Game/VoyageCaptureService.cs`.

## Argus

<https://github.com/ofnature/Argus> · Copyright (c) 2026 ofnature · MIT License

- Noticing a finished voyage from the voyage result window opening, with no game hook. Used in `SubLedger/Game/VoyageCaptureService.cs`.
- The general approach to hand-drawn interface components (sidebar entries, switches, status pills, panels whose background is painted behind their content) in `SubLedger/Ui/Widgets.cs`.

## Libraries

- **Dalamud** and **FFXIVClientStructs** (<https://github.com/goatcorp/Dalamud>, <https://github.com/aers/FFXIVClientStructs>): the plugin framework and the game structure definitions.
- **Lumina** (<https://github.com/NotAdam/Lumina>): reads the game's item and sector data.
- **Microsoft.Data.Sqlite** and **SQLitePCLRaw** (MIT and Apache-2.0), with **SQLite** (public domain): the ledger's database engine.

## Game data

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Item names, vendor prices and sector names are read from the player's own game client at run time; none are distributed with SubLedger.

---

The MIT License, under which SubmarineTracker, BunnySubSync and Argus are published:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
