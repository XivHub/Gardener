# Gardener

Keeps track of your garden beds, reminds you when they need care, and tends, fertilizes or
harvests a whole patch for you.

Works with outdoor garden patches (Deluxe, Oblong and Round) on personal, Free Company and shared
estates. Indoor flowerpots aren't supported yet.

> **Warning:** this automates gameplay, which breaks the FFXIV User Agreement and can get your
> account penalized or banned. Use it at your own risk.

## Garden

Stand at your plot and Gardener shows what's growing in each bed and how far along it is, without
opening any menus. It remembers when each bed was planted and last tended, for every character on
your account.

## Reminders

The Reminders tab and the server info bar tell you, from anywhere, which beds need tending, which
are about to wither, and which are ready to harvest. Each one says which character can reach that
garden, so you know who to log in.

## One-click care

At the plot, the Garden tab has buttons to tend, fertilize or harvest every bed. Gardener checks it
has the right bed before each action, and stops if you walk away or a cutscene starts.

Want to keep a crop, like a crossbreeding parent? Right-click its name and tick "Don't harvest".
Harvest runs skip it until you replant that bed.

## Crossbreeding

Pick the seed you want in the Goal tab. Gardener shows how to get there: which seeds to plant next
to each other, which soil to use, and the odds. It can plant a step for you when you ask.

Gardener never moves your character. It only acts at the plot you're standing at.

## Install

In `/xlsettings`, under Experimental, add this to Custom Plugin Repositories:

```
https://plugins.xivhub.net/pluginmaster.json
```

Then install Gardener from the plugin installer.

## Commands

| Command | What it does |
|---|---|
| `/gardener` | Open the main window |
| `/gardener dump` | Print what Gardener sees, for bug reports |
| `/gardener dump menu` | The same, with a bed's menu open |

## Credits

- Crossbreed data adapted from
  [nick75g/FFXIV-Crossbreed-Helper](https://github.com/nick75g/FFXIV-Crossbreed-Helper) (MIT).
- Grow times adapted from
  [Lotlab/FFXIV-Gardening-Tracker](https://github.com/Lotlab/FFXIV-Gardening-Tracker) (GPL-3.0).
- Crossbreed and yield data from the Big Triangle and Gardening Spreadsheets kept by the FFXIV
  gardening community. See also [ffxivgardening.com](https://www.ffxivgardening.com).

Full notices are in `NOTICE`. Gardener contains no code or data from
[Lunauryae/Gaia](https://github.com/Lunauryae/Gaia).

Building from source: see [DEVELOPMENT.md](DEVELOPMENT.md).

## License

AGPL-3.0-or-later. See `LICENSE` and `NOTICE`.
