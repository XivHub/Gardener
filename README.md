# Gardener

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that keeps a journal
of your outdoor garden patches, reminds you when a bed needs attention, and tends, fertilizes or
harvests every bed while you stand at the plot.

It covers Deluxe, Oblong and Round patches on personal, Free Company and shared estates. Indoor
flowerpots are not supported.

> **Warning:** this automates gameplay, which breaks the FFXIV User Agreement and can get your
> account penalized or banned. Use it at your own risk.

## Features

### Journal

Gardener finds every outdoor patch on the plot you are standing on and reads each bed's crop and
growth stage from game memory, without opening a menu. The game does not expose when a bed was
planted or last tended, so Gardener records those the moment it sees them happen. The journal is
shared by every character on the account, since a garden belongs to the plot.

### Reminders

The Reminders tab and a server info bar entry work from anywhere in the world. They list beds due
for tending, beds close to withering, beds that look ready to harvest, and beds Gardener has not
observed closely enough to judge. Each due bed names the character last seen with access to it, so
you know which alt to log in.

### Sweeps

At the plot, the Garden tab has buttons to tend, fertilize or harvest all beds. Before each action the
sweep checks the bed number the game reports. It stops when you walk away, a cutscene starts, or an
expected menu fails to open. Right-click a crop name to mark the bed "Don't harvest", for example a
crossbreeding parent you want to keep; harvest sweeps then skip it until the bed is replanted.

### Crossbreed planner

The Goal tab takes a seed you want and lays out the route to it: which seeds to obtain, what to
plant next to what, in which soil, and the odds. Gardener can plant one step of the route when you
ask. It never runs the whole chain on its own.

Gardener does not teleport, mount or move your character. Automation only runs at the plot you
walked to.

## Install

Gardener is in the XIV Hub plugin repository. In `/xlsettings`, under Experimental, add this to
Custom Plugin Repositories:

```
https://plugins.xivhub.net/pluginmaster.json
```

Then install Gardener from the plugin installer.

## Commands

| Command | Effect |
|---|---|
| `/gardener` | Open the main window (Garden, Goal, Reminders and Log tabs). |
| `/gardener dump` | Print a diagnostic dump of the discovered patches and their beds. |
| `/gardener dump menu` | The same dump, taken with a bed menu open. |

Reminders and the server info bar entry run as soon as the plugin loads. The sweep buttons appear
only while you are within reach of a discovered patch.

## Build

Requires the .NET SDK and an extracted Dalamud dev bundle.

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build Gardener/Gardener.csproj -c Release -p:Platform=x64
```

The build writes `Gardener/bin/x64/Release/Gardener.dll` and a packaged `latest.zip`.

## Publish

Bump `<Version>` in `Gardener/Gardener.csproj` so Dalamud sees an update, then run `./publish.sh`
from the repo root. It drift-checks the bundled seed and crossbreed tables
(`tools/build_data.py --check`) and the Spanish strings (`tools/check_loc.py --check`), then calls
`publish-plugin`. That helper builds Release and replaces Gardener's entry in the combined Zhyra
`pluginmaster.json`, leaving the other plugins' entries alone.

## Credits

- Crossbreed dataset adapted from
  [nick75g/FFXIV-Crossbreed-Helper](https://github.com/nick75g/FFXIV-Crossbreed-Helper) (MIT).
- Per-seed grow times adapted from
  [Lotlab/FFXIV-Gardening-Tracker](https://github.com/Lotlab/FFXIV-Gardening-Tracker) (GPL-3.0).
- Crossbreed validation matrix and wilt and yield data from the Big Triangle and Gardening
  Spreadsheets maintained by the FFXIV gardening community. See also
  [ffxivgardening.com](https://www.ffxivgardening.com).

`NOTICE` has the full notices, including the MIT and GPL-3.0 texts.

Gardener contains no code or data from [Lunauryae/Gaia](https://github.com/Lunauryae/Gaia).

## License

AGPL-3.0-or-later. See `LICENSE` and `NOTICE`.
