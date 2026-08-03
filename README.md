# TrickyMaddnessLevelHook
 
## Custom map thumbnails

A custom map can supply its own level-select thumbnail. Drop a PNG next to the
map's `.asset` in `Maps/`, named to match it — `Maps/MyMap.asset` picks up
`Maps/MyMap.png`. The card art we author is 1024x576; the image is drawn with
its aspect ratio preserved, so other sizes work but anything far off 16:9 will
letterbox inside the card.

This is optional. Without a PNG the map keeps the level-select card's default
placeholder art.

## Custom maps across platforms

A map's `.asset` bundle stores its shaders as compiled bytecode for whichever
platform it was built on. The bundle still loads on the other platforms, but
none of that bytecode runs there, so the whole map renders magenta.

The hook fixes this by rebinding the map's materials onto the game's own
shaders, which are the same ones by name and are always correct for the
platform you are actually running. Some shader features can be lost in the
process, so it only does this when it has reason to think the map is foreign.

Config entry `[Rendering] ShaderRemap` (`BepInEx/config/TrickyMaddnessLevelHook.cfg`):

- `Auto` (default) — remap only when the game is *not* running on Windows.
  Custom maps are overwhelmingly built on Windows, so Windows players keep
  their maps exactly as authored and macOS/Linux players get maps that work.
- `Always` — also remap on Windows. Use this if a map built on macOS or Linux
  renders magenta for you.
- `Never` — disable it entirely.

The setting above is a guess based on where the game is running, because nothing
in a bundle records the platform it was built for. Map authors can replace that
guess with the truth — see below.

## Map settings sidecar

A map can describe itself to the hook with a plain text file next to its
`.asset` — `Maps/MyMap.asset` picks up `Maps/MyMap.hook.txt`. Each line is a
key and a value:

```
platform mac
source my_conversion_project
mapversion 1.2
```

Every key is optional and unknown lines are ignored, so a map with no sidecar,
or with a sidecar carrying data the hook doesn't know about, works exactly as it
did before. A `#` at the start of a line comments it out.

- `platform` (`windows` | `mac` | `linux`) — which platform this bundle's
  shaders were compiled for. Declaring it replaces the platform *guess* the
  shader remap otherwise has to make: a map that says `platform mac` is remapped
  on Windows automatically, with no config change asked of the player, and left
  untouched on macOS where its shaders really are native. **If you publish a map
  built anywhere other than Windows, set this** — it is the difference between
  your map just working and every player having to be told to flip a setting.
  Any other value is ignored with a warning in the log, and the hook goes back
  to guessing.
- `source` — free-form label for where the level came from. Logged only.
- `mapversion` — which build of the level this is. Logged only.

`source` and `mapversion` change no behaviour at all; they exist so that a
player's pasted log says which build of which map they were on.

`platform` is only consulted in `ShaderRemap = Auto`, which is what makes it
safe — `Always` and `Never` are explicit player overrides and still win. If you
previously set `Always` to make a macOS- or Linux-built map render, you can put
it back to `Auto` once that map ships a `platform` line; leaving it on `Always`
will remap maps that don't need it.

### Sidecar filenames

Two names are read, in this order:

1. `MyMap.hook.txt` — the name to use for anything new.
2. `MyMap.aipaths.txt` — read for compatibility. A family of already-published
   converted maps ships this file with exactly these headers already in it, so
   reading it means those maps declare their platform to the hook without
   needing to be repackaged and re-uploaded. If both files exist,
   `MyMap.hook.txt` wins and the other is not read.

Whichever name is used, the contents are the same and only the keys documented
above have any effect.
