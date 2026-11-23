# Development

## Build

Requires the .NET SDK and an extracted Dalamud dev bundle. Shared helpers come from
[XivHubPluginKit](https://github.com/XivHub/XivHubPluginKit), linked by source: clone it next to this
repo, so `../XivHubPluginKit` exists.

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build Gardener/Gardener.csproj -c Release -p:Platform=x64
```

The build writes `Gardener.dll` and a packaged `latest.zip` under `Gardener/bin/x64/Release/`.

## Publish

Bump `<Version>` in `Gardener/Gardener.csproj` so Dalamud sees an update, then run `./publish.sh` from the repo
root. It first drift-checks the bundled seed and crossbreed tables (`tools/build_data.py --check`)
and the Spanish strings (`tools/check_loc.py --check`), then calls `publish-plugin`. It builds Release and replaces Gardener's entry in the XIV Hub `pluginmaster.json`, leaving the
other plugins alone.
