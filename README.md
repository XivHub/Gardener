# Gardener

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that tracks your
**outdoor garden patches** — Deluxe, Oblong and Round beds on personal, Free Company and shared
estates — in a persistent journal, reminds you when a bed needs attention no matter where you are
in the world, and sweeps the plot for you while you are standing at it.

> ⚠️ **This automates gameplay, which violates the FFXIV User Agreement and can get your account
> penalized or banned. Use entirely at your own risk.**

## What it does

- **Journal.** Discovers every outdoor patch on the plot you are standing on and reads what is
  planted in each bed straight from the game's own memory — no menus opened just to look. What the
  game does not expose (when a bed was planted, when it was last tended) is recorded the moment
  Gardener sees it happen, and kept for every character on the account, because a garden belongs to
  the plot, not to whoever is standing in it.
- **Reminders.** The Reminders tab and a server info bar entry read that journal from anywhere in
  the world: which beds are due to be tended, which are close to withering, which look ready to
  harvest, and which have never been observed closely enough to say. A due bed always names the
  character who was last seen able to reach it, so "switch to Alice" replaces a garden you cannot
  currently act on.
- **Sweeps.** While standing at the plot, tend-all, fertilize-all and harvest-all work through every
  eligible bed, verifying the bed number the game reports before every single action and stopping
  the moment you walk away, a cutscene starts, or the addon it expected does not show up.
- **Never travels.** Gardener does not teleport, mount, or path you anywhere. Automation only ever
  runs at the plot you already brought your character to.

## Install

Gardener ships in the combined Zhyra plugin repository. Add it to Dalamud:

```
/xlsettings → Experimental → Custom Plugin Repositories
https://edgl.dev/share/zhyra/pluginmaster.json
```

Then install **Gardener** from the plugin installer (it appears alongside the other Zhyra plugins).

## Usage

- `/gardener` — open the main window (Garden, Plan, Reminders and Log tabs)
- `/gardener dump` — print a diagnostic dump of discovered patches and their beds
- `/gardener dump menu` — the same dump, taken with a bed menu open

Reminders and the server info bar entry run continuously once the plugin is loaded; nothing needs
to be started for them to work. Tend-all, fertilize-all and harvest-all buttons appear on the
Garden tab only while you are standing within reach of a discovered patch.

## Build

Requires the .NET SDK and an extracted Dalamud dev bundle.

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build Gardener/Gardener.csproj -c Release -p:Platform=x64
```

Output: `Gardener/bin/x64/Release/Gardener.dll` and a packaged `…/Gardener/latest.zip`.

## Publish / deploy

Deploy with the shared, plugin-agnostic helper (in `~/.local/bin`), run from the repo root:

```bash
publish.sh          # runs tools/build_data.py --check, then publish-plugin
```

It regenerates and drift-checks the bundled seed and crossbreed tables, builds Release, stages
`latest.zip` + `Gardener.dll` under `~/share/zhyra/gardener/`, and **merges** Gardener's entry into
the combined Zhyra `pluginmaster.json` (replacing only its own entry, keeping the other plugins).
The plugin has no separate repo. **Bump `<Version>` in the csproj before publishing** so Dalamud
detects the update.

## Documentation

- `docs/RESEARCH.md` — the mechanics and data behind the plugin.
- `docs/PLAN.md` — the full design/implementation plan.
- `AGENTS.md` — architecture map and gotchas for anyone (human or AI) changing this code.

## Credits

- Crossbreed working dataset adapted from
  [nick75g/FFXIV-Crossbreed-Helper](https://github.com/nick75g/FFXIV-Crossbreed-Helper) (MIT).
- Per-seed grow-time table adapted from
  [Lotlab/FFXIV-Gardening-Tracker](https://github.com/Lotlab/FFXIV-Gardening-Tracker) (GPL-3.0).
- Crossbreed validation matrix and wilt/yield data from the Big Triangle and Gardening Spreadsheets
  maintained by the FFXIV gardening community; see also [ffxivgardening.com](https://www.ffxivgardening.com).

Full notices, including the MIT and GPL-3.0 texts, are in `NOTICE`.

**Gardener contains no code or data from [Lunauryae/Gaia](https://github.com/Lunauryae/Gaia).**

## License

AGPL-3.0-or-later. See `LICENSE` and `NOTICE`.
