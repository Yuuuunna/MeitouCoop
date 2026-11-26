# Launch options and environment variables

Every command-line option and environment variable read by the project's executables. The parsing files named in each section are the source of truth.

### How Meitou finds settings and your Kenshi install

- User settings are saved in `meitou.user.json`.
- Meitou looks for your Kenshi installation in this order:
  1. The `KENSHI_PATH` environment variable.
  2. `meitou.local.json` in the current working directory, the executable's directory, or any parent directory.
  3. On Linux and macOS, `XDG_CONFIG_HOME/meitou/config.json` (`kenshiPath`).
  4. Automatic Steam/platform detection.
  If none of these work, the game prompts you and saves your choice to `meitou.local.json` (`src/Meitou.Core/Content/GameInstall.cs`, `GameInstallSources.cs`).
- Passing a flag that belongs to a different executable exits with code `2`.

Player-test diagnostics: `MEITOU_DIAGNOSTICS_DIRECTORY` overrides the client/launcher diagnostic root;
`MEITOU_DIAGNOSTICS_URL` selects an explicit private HTTPS collector for client/launcher/server. Server diagnostic files remain under
its configured data directory. Runtime switches, collector service variables and retention: [diagnostics](diagnostics.md).

## Launcher (`meitou-launcher`)

**Observed** (`launcher/Meitou.Launcher/Program.cs`): `--software-rendering` or `MEITOU_LAUNCHER_SOFTWARE_RENDERING=1`
forces CPU UI rendering and skips automatic client GPU preset detection. Requires a desktop session; it does not make the game headless.
Dedicated server selection and controls: [launcher](launcher.md#hosting-and-cpu-rendering).
Automatic Connect mod profiles and the child-only `MEITOU_MODS_DIRECTORY`, `MEITOU_MODS_CFG`, `MEITOU_SERVER_MOD_PROFILE` variables:
[mod-sync](mod-sync.md). `MEITOU_MODS_DIRECTORY` replaces discovery completely; a missing explicit directory is an error.

## Game (`meitou`, `MeitouClient.exe`)

Parsed in `src/Meitou.Game/Host/GameOptions.cs` and `Program.cs`. Any flags not handled here fall through to [Shared world options](#shared-world-options). Run with `-h` or `--help` for usage.

### Start and session

The Vulkan title also offers [Multiplayer](networking.md#graphical-admission): enter a server address, choose an offered beginning and edit appearances before entering. CLI entry remains available.

| Option | Value | Default | Effect |
|---|---|---|---|
| `--new-game [start]` | start name | `Wanderer` | Jumps straight into a new game with your squad at its starting town. |
| `--list-starts` | | | Lists all available game starts and exits. |
| `--mods-cfg <file>` | file path | `data/mods.cfg` | Loads enabled mods, load order, language, and code mods from a custom file instead of `data/mods.cfg` (relative paths resolve from the working directory; exits with code `2` if missing). Overrides `MEITOU_MODS_CFG`. Read-only: the launcher still writes `data/mods.cfg`. |
| `--data-check` | | | Scans the active load order for data and level file errors (missing mods, changed record types, etc.) and exits (`0` = clean, `1` = problems found). |
| `--load <folder>` | save folder | none | Loads a Meitou or vanilla Kenshi save directly. Works with `--screenshot`. |
| `--continue` | | off | Loads the newest save across all save folders (`SaveModList.Roots`) without verifying that its mods match the current load order. Cannot be combined with `--load`; exits with code `1` if no save exists. |
| `--seed <n>` | integer | `0` | Sets the world RNG seed. Persists if you start a new game from the main menu. |
| `--no-population` | | off | Disables NPC spawning and movement. |
| `--move-to <x> <z>` | two numbers | none | With `--new-game`: selects the starting squad and orders it to walk to `(x, z)` (useful for scripted screenshots). |
| `--ticks <n>` | integer | `0` | With `--screenshot`: runs `n` simulation ticks before capturing. |
| `--quit-after <s>` | seconds | none | Exits automatically after `s` seconds and prints the frame rate (smoke test). |
| `--no-precompile` | | off | Disables background JIT precompilation; methods compile on first use instead. |
| `--play-bench <dir>` | folder | none | Runs a scripted benchmark, logs every frame to `frames.csv` and `summary.md`, and exits. Forces unlimited FPS and vsync off (a later `--vsync` turns vsync back on). Works with `--load` or `--new-game` on Vulkan and Metal ([bench](bench.md#play-benchmark)). |
| `--benchmark <tier>` | `low`, `medium`, `high`, `ultra`, `extreme` | none | Runs the launcher benchmark at the given quality tier using default settings (ignores and doesn't touch `meitou.user.json`). Requires `--benchmark-out`. Exit codes: `0` ok, `5` GPU lacks ray queries required by tier, `6` wrong save for scenario, `7` unknown scenario ([bench](bench.md#launcher-benchmark)). |
| `--benchmark-scenario <id>` | `bases`, `shark-flyby` | `bases` | Selects the benchmark scenario. `bases` requires `--load` with its save; `shark-flyby` forbids `--load`. |
| `--benchmark-out <dir>` | folder | none | Output directory for `--benchmark` (`frames.csv`, `summary.md`, `result.json`). |
| `--benchmark-list <file>` | file path | | Writes benchmark version and available scenarios as JSON and exits. Must be the only arguments. |
| `--benchmark-probe <file>` | file path | | Probes hardware via a headless Vulkan device, writes a JSON report, and exits. Must be the only arguments. |
| `--settings-schema <file>` | file path | | Writes the launcher's settings schema and exits. Must be the first two arguments; creates no log or window. |
| `--recommend-preset <file>` | file path | | Writes the recommended graphics preset for this hardware (`HardwareProbe`, [launcher](launcher.md#graphics)) as JSON and exits. Must be the first two arguments; creates no log or window. |
| `--wiki-export <dir>` | folder | | Exports game data (zones, towns, factions, squads, NPCs, items, buildings, shops, characters, research, animals, spawns, including active mods) as JSON for agent playtesting and exits ([agent playtest](agent-playtest.md)). |
| `--facts-only` | | off | With `--wiki-export`: strips description text and exports only names, IDs, numbers, coordinates, conditions, and relations. |

### Agent channel (debug)

Used by automated playtesting ([agent playtest](agent-playtest.md)); not intended for normal play or benchmarks.

| Option | Value | Default | What it does |
|---|---|---|---|
| `--control-dir <dir>` | folder | `MEITOU_CONTROL_DIR` | Watches `<dir>/cmd` for `<id> <console command>` lines and writes results to `<dir>/out`. |
| `--exec "<cmd; cmd>"` | console commands | none | Runs console commands in order once the world loads and settles. If a command fails, prints `setup failed: ...`, skips the rest, and exits with code `3`. Alias: `--setup`. |
| `--screenshot-after <s>` | seconds | none | Waits `s` seconds after setup, saves a screenshot, and exits (`0` ok, `3` setup failed, `4` screenshot failed). |
| `--screenshot-out <png>` | file path | `<control-dir>/shot.png` or `./meitou-shot.png` | Output file for `--screenshot-after`. |

### Graphics and UI

| Option | Value | Default | What it does |
|---|---|---|---|
| `--fps-limit <n>` | integer | `150` (`meitou.user.json`) | Caps frame rate when vsync is off (`0` = unlimited). |
| `--vsync` / `--no-vsync` | | `meitou.user.json` (off) | Overrides vsync for this session. |
| `--hdr` / `--no-hdr` | | `meitou.user.json` (off) | Enables or disables HDR10 output for this session ([post](render-post.md#hdr-output)). |
| `--renderer <vulkan\|metal>` | `vulkan` or `metal` | `vulkan` | Chooses the graphics backend. Metal is macOS only; the launcher sets this automatically. |
| `--metal-gpu-budget <MB>` | MB (> 0) | `MEITOU_METAL_GPU_BUDGET_MB`, or 1/3 of Mac RAM (`5120` to `10240`) | Sets Metal's GPU memory guard ceiling for this session. Overrides the environment variable. |
| `--ui <faithful\|meitou>` | `faithful` or `meitou` | `meitou.user.json` (`meitou`) | Switches UI style independently of graphics settings. |
| `--ui-showcase` | | off | With `--screenshot`: captures HUD, inventory, stats, map, menus, trade, and dialogue in the active UI style. |
| `--ui-panel <name>` | panel name | none | With `--screenshot`: opens a specific UI panel (`hud`, `hud-expanded`, `inventory`, `stats`, `overview`, `menu`, `title`, ...). |
| `--settings-showcase` | | off | With `--screenshot`: captures screenshots of the Settings Graphics page (tabs, presets, Save As, Low quality, Faithful). |
| `--title-preview` | | off | With `--screenshot`: renders the staged Meitou title screen (forces Meitou UI and main menu style). |

### Sandbox, scenes, duel, and battle

| Option | Value | Default | What it does |
|---|---|---|---|
| `--sandbox [n]` | `1` to `1000` | off (`1` if no number given) | Launches an animation sandbox on a flat wireframe floor. `n = 2` places two characters facing each other; extras stand idle in rings around them. |
| `--sandbox-character <name>` | `CHARACTER` or `ANIMAL_CHARACTER` name or ID | first character of default start | Picks which character spawns in the sandbox. |
| `--sandbox-mode <list>` | comma list: `walk`, `run`, `free`, speed in u/s, `patrol`, `fight` | idle | Initial behavior for the sandbox character (e.g. `run,patrol`). |
| `--sandbox-races` | | off | Spawns one character of every humanoid race in rings around the center pair. |
| `--sandbox-animals` | | off | Spawns one animal of every race used by `ANIMAL_CHARACTER` records (first record per race). |
| `--terrain-sandbox [cliffs] [x,z]` | | flattest spot `(38376,-8280)`; cliffs `(92164,123124)` | Loads an empty world displaying a test grid of terrain texture tiles (`cliffs` shows cliff textures on a steep slope). |
| `--duel [x,z]` | | dune crest | Plays a looping, scripted two-Samurai duel at sunset. Locks time to `22:00` and clear weather unless `--time` or `--weather` is passed. |
| `--battle <n> <unit> <m> <unit> [rows]` | counts >= 1; `CHARACTER` or `ANIMAL_CHARACTER` name or ID | rows of `25` | Spawns two opposing NPC formations to fight each other. Press Backspace to restart. Incompatible with `--new-game`, `--load`, `--no-population`, `--duel`, and `--sandbox`. |
| `--battle-map <map>` | `plateau`, `mesa`, `fog`, `sandbox`, or `x,z` | `plateau` | Battle arena location (`mesa` = duel spot, `fog` = Fog Islands plateau, `sandbox` = flat wireframe floor). |
| `--battle-width <a>[,<b>]` | counts | `rows` value | Fighters per row for each side (a single number applies to both). |
| `--battle-no-fear` | | off | Prevents fighters from fleeing during a battle. |
| `--scene <name>` | `fight`, `turret`, `bed`, `acid`, `menu` | none | With `--screenshot` and `--load`: stages a test scenario and saves a sequence of frames. |
| `--scene-frames <n>` | `1` to `200` | `8` | Number of frames to capture in `--scene`. |
| `--scene-every <n>` | >= 1 | `30` | Simulation ticks between captured frames. |
| `--scene-npc <record>` | `CHARACTER` name or ID | `Bandit` | Enemy type spawned in the `fight` and `turret` scenes. |
| `--scene-town <name>` | partial town name | nearest turret | Town to move the squad to before running the `turret` scene. |

## Viewer (`meitou-viewer`)

Parsed in `tools/Meitou.ModelViewer/Program.cs`, `WorldApp.cs`, `WorldApp.FieldTour.cs`, `ImpostorApp.cs`, and `WorldCommandLine.cs`. Running with no arguments opens `--world --town "The Hub"`.

Mode dispatch: `--world`, `--view`, and `--bench-compare` launch the world viewer; `--impostor-preview` and `--impostor-bake-all` launch the impostor tool; anything else prints usage and exits with code `2`.

### World viewer (`--world`)

| Option | Value | Default | What it does |
|---|---|---|---|
| `--world` | | | Opens the world viewer. Accepts any [Shared world option](#shared-world-options). |
| `--view <name>` | `swamp`, `swamp-rain`, `swamp-low`, `dust`, `hub` | none | Loads a named camera, location, and weather preset (`NamedViews.cs`). Later flags override the preset. |
| `--quit-after <s>` | seconds | none | Exits after `s` seconds and prints total frames and average FPS. |
| `--terrain-sandbox [cliffs] [x,z]` | | flattest spot (`cliffs` sets yaw `180`) | Opens an empty world with a terrain texture grid; displays the tile name under the camera in the top-left corner. |
| `--field-tour <dir>` | folder | none | With `--terrain-sandbox`: renders a screenshot of every terrain tile and generates an `index.html` gallery. |
| `--tour-distance <u>` | units | `120` | Camera distance during `--field-tour`. |
| `--tour-pitch <deg>` | degrees | `30` | Camera pitch during `--field-tour`. |
| `--relief <Name=value>` | repeatable | default values | Overrides a `GroundRelief` or `RockSmoothing` parameter for the run (e.g. `TargetSlope=0.3`). |

### Impostor tool (`--impostor-preview`, `--impostor-bake-all`)

| Option | Value | Default | What it does |
|---|---|---|---|
| `--impostor-preview <mesh>` | mesh name | none | Bakes and previews the distant impostor for a single mesh. |
| `--impostor-bake-all` | | off | Bakes impostors for all meshes. |
| `--rebake` | | off | Forces re-baking even if cached impostors already exist. |
| `--class <name>` | class name | none | Filters by impostor class (see `ImpostorApp.cs`). |
| `--out <dir>` | folder | `C:\Temp\meitou-impostors` | Output folder for previews and baked files. |
| `--size <W>x<H>` | pixels | `1600x900` | Preview image size. |
| `--debug <n>` | integer | `0` | Debug output verbosity level. |

## Shared world options

Parsed in `src/Meitou.Game/Rendering/Frame/WorldCommandLine.cs`, `PostProcessOptions.cs`, and `Enhancements.cs`. Supported by both the game and the viewer.

When an option maps to a settings row, it overrides that setting for the current session only without reading or overwriting `meitou.user.json` (`GameHost.RunOnlyKeys`). Startup-only settings (like water grid and ocean FFT resolution) are read before loading the world; all others can change live.

### Location, camera, and window

| Option | Value | Default | What it does |
|---|---|---|---|
| `--at <x>,<z>` | world X,Z | world center | Points the camera at `(x, z)`. |
| `--zone <i>,<j>` | zone coordinates | | Centers on zone `(i, j)` (`i` along `+X`, `j` along `+Z`). |
| `--town <name>` | partial town name | `The Hub` (game) | Starts at the first town whose name matches. |
| `--camera-code <hex>` | camera code or report string | | Jumps to a camera code copied from the viewer (`Ctrl+C`) or a feedback report location (`target x, y, z; ...; yaw a, pitch b, boom d`). |
| `--radius <zones>` | zones | `1.5` | Half-width of the loaded zone square (`4608` world units per zone). |
| `--yaw <deg>` / `--pitch <deg>` | degrees | `30` / `35` | Starting camera heading and pitch above the horizon. |
| `--distance <u>` | units | matches radius | Camera boom distance. |
| `--fly-to <x>,<z>` | world X,Z | | With `--screenshot`: moves the camera to `(x, z)` before capturing. |
| `--windowed` | | off (fullscreen) | Opens a bordered window (maximized unless `--size` is given). |
| `--hidden-window` | | off | Creates an invisible window (implies `--windowed`) for unattended runs like `--play-bench`. Renders and presents normally. |
| `--headless` | | off | Runs without a window, swapchain, or audio, rendering offscreen at `--size` (default `1920x1080`, 30 FPS unless `--fps-limit` is set). Designed for the agent channel. Also enabled by `MEITOU_HEADLESS=1`. |
| `--monitor <n>` | 1-based index | the Monitor setting | Starts the window and its full screen on monitor `n` (GLFW's order) for this run; a missing one is automatic ([viewer](viewer.md), Alt+Enter). |
| `--show-keys` | | off | Opens the F10 key list on startup. |
| `--play-shots` | | off | Continuously flies through saved camera bookmarks in a loop. |
| `--crowd <n>` | count | `0` | Spawns `n` generated test characters in the starting town. |

### Output, resolution, and debugging

| Option | Value | Default | What it does |
|---|---|---|---|
| `--screenshot <out.png>` | file path | none | Renders offscreen to a PNG file and exits. |
| `--size <W>x<H>` | pixels | `1280x960` (`1280x720` for bench/A/B) | Render resolution. |
| `--no-vsync` | | vsync on (viewer) | Starts with vsync off. (In the game, handled under Graphics and UI.) |
| `--log-spikes` | | off | Logs frame spikes where GPU time or render-thread time (> 20 ms) exceeds 1.5x the median, plus a `simprof` breakdown every 5 seconds. |
| `--sway-step <s>` | seconds | | Advances grass sway by `s` seconds per frame (for motion checks with screenshots or benchmarks). |
| `--orbit-step <deg>` | degrees | | With `--screenshot`: orbits the camera by `deg` each frame to test upscaler motion vectors. |
| `--wireframe` | | off | Renders geometry in wireframe mode. |
| `--action-log <who>` | `off`, `squad`, or `everyone` | `off` | Logs character task changes and reasons to `log_actions.txt` next to `log.txt` ([simulation](simulation.md), "Action log"); can also be toggled in the F10 menu. |
| `--debug <n>` | `1` or `2` | off | Terrain debug visualization: `1` = blend-map slot weights, `2` = layer weights. |
| `--debug-shadows <n>` | `1` to `4` | off | Shadow debug view: `1` = four cascade maps, `2` = shadow factor by cascade, `3` = shadow factor multiplied on scene, `4` = ray-traced sun view (blue cascades, green lit, red shadowed) with trace reason logs. |
| `--help` / `-h` | | | Prints command-line usage. |

### Time, weather, audio, and rendering

| Option | Value | Default | What it does |
|---|---|---|---|
| `--time <hour>` | hours | `13` | Sets the sun's time of day. |
| `--time-speed <h/min>` | game hours per real minute | `0` (paused) | Sets how fast the world clock advances. |
| `--day <n>` | game day | `0` | Starting day for the weather schedule. |
| `--weather <name\|auto>` | `auto` or `WEATHER` name | `auto` | Runs the weather scheduler (`auto`) or locks a specific weather record. |
| `--clouds <0..1>` | density | from weather | Overrides cloud cover density. |
| `--no-particles` | | off | Disables weather particles. |
| `--particle-prewarm <s>` | seconds | longest particle life | Simulates particles for `s` seconds before the first frame (`0` starts empty). |
| `--particle-density <x>` | `0.1` to `1` | `1` | Scales weather particle emission rate. |
| `--no-audio` | | off | Disables all audio. |
| `--audio-gain <dB>` | dB | `0` | Adjusts overall audio mix volume (`0` = default game level). |
| `--no-objects` | | off | Hides buildings and map features. |
| `--no-foliage` | | off | Hides trees, bushes, rocks, and grass. |
| `--grass-density <x>` | `0.1` to `2` | `1` | Scales grass blade density. |
| `--no-water` | | off | Disables water rendering. |
| `--water-grid <n>` | `64` to `1024` | `1024` | Water mesh grid segments around the camera (Graphics > World > Water, steps of 64, startup only). |
| `--water-ocean <n>` | `64`, `128`, or `256` | `256` | Ocean wave FFT resolution per cascade; defaults to `128` on integrated GPUs (Water wave resolution, startup only). |
| `--water-reflection <0..4>` | `0` to `4` | `4` | Water reflection detail (`0` = sky only, `4` = includes trees and rocks). |
| `--reflection-samples <n>` | samples | `4` | Samples per texel for half-res reflections (Reflection samples: off, 2x, 4x, 8x, live). |
| `--texture-quality <0..4>` | `0` to `4` | `0` | Drops `n` top mip levels from compressed textures (`0` = full resolution). |
| `--anisotropy <n>` | `1` to `16` | `16` | Maximum anisotropic texture filtering. |
| `--shadow-quality <0\|1\|2>` | `0`, `1`, or `2` | `2` | Shadow map resolution: `1024`, `2048`, or `4096`. |
| `--shadow-range <u>` | units (`0` = off) | `5000` (`10000` with Meitou shadows) | Shadow draw distance, clamped to `1000..9000` (or `1000..15000` with Meitou shadows). |
| `--shadow-filter <0\|1\|2>` | `0` to `2` | `2` | Meitou shadow filter quality (`0` and `1` are cheaper for low-end GPUs). |
| `--no-gi` | | GI on if ray queries supported | Disables ray-traced probe global illumination. |
| `--gi-debug <0..3>` | `0` to `3` | `0` | GI debug overlay: `1` = hit distance, `2` = one-bounce light, `3` = normals. |
| `--gi-hybrid <parts>` | comma list: `relocate`, `clouds`, `cache` | off | Enables `gihybrid` with only the listed sub-features ([render-gi](render-gi.md)). Maps to Lighting > Indirect light rows (live). |
| `--fog <u>` | units | `250000` | Distance where atmospheric haze reaches 100% (Sky & weather > Haze, `10000..1000000`, live). |
| `--material-distance <u>` | units | `30000` | Distance beyond which terrain switches to biome ground colour (World > Draw distances, `2000..100000`, live). |
| `--terrain-error <px>` | pixels | `16` (Meitou), `10` (Faithful) | Maximum allowed terrain LOD pixel error near the camera. |
| `--object-distance <u>` | units | `20000` (Meitou), `12000` (Faithful) | Full-detail draw distance for placed objects. |
| `--range-large <u>` | units | `50000` | Draw range for large foliage. |
| `--distant-range <zones>` | zones | `10` | How far distant towns render (`0` disables them). |
| `--impostor-budget <MB>` | MB | 8% VRAM (5% integrated), `48` to `1024` | VRAM cap for resident impostor atlases (World > Far impostors, `0..2048` MB in 16 MB steps; `0` = auto, live). |
| `--no-load-cache` | | off | Skips reading and writing load caches (foliage layouts, character LODs, world colour map, terrain colour tiles). |
| `--cache-root <dir>` | folder | `%LOCALAPPDATA%\Meitou` | Moves all derived caches (LODs, impostors, shaders, pipelines, terrain tiles, navmesh, audio codebooks, item icons) into this directory. Settings, saves, and photo presets stay put. Individual `MEITOU_*_CACHE` variables still take precedence. |
| `--low-end` | | off | Low-end PC preset: short draw distances, no GI/shadows/reflections/AO/relief/light shafts/AA, `0.83` render scale, sparse grass and particles, simple terrain shading, and Faithful UI/main menu (leaves saved settings untouched). Flags placed after it override graphics options. |
| `--benchmark-baseline` | | off | Locked Vulkan benchmark configuration: `1920x1080` window (implies `--windowed`, later `--size` overrides), Medium tier, no GI (no ray queries), no ground parallax or tessellation, no upscaler, SMAA at native scale. Ignores and preserves saved settings. Later graphics flags are ignored in the game (honored in the viewer). Vulkan only (Metal applies saved settings over it). |

### Post-processing and upscaling (`PostProcessOptions.cs`)

| Option | Value | Default | What it does |
|---|---|---|---|
| `--post <meitou\|kenshi\|off>` | preset | `meitou` | Selects the post-processing chain preset (applied before the options below). |
| `--upscaler <off\|taa\|fsr\|dlss\|metalfx>` | upscaler | `taa` (Meitou), `off` (Faithful) | Chooses temporal upscaling. FSR and DLSS require vendor libraries; MetalFX is macOS Metal only (falls back to TAA). |
| `--render-scale <0.25..1 or name>` | `0.25..1` or `native`, `ultra-quality`, `quality`, `balanced`, `performance`, `ultra` | `0.67` (Quality) with Meitou upscaler, else `1` | Internal render resolution multiplier per axis. |
| `--no-fxaa` | | FXAA on | Disables FXAA when no temporal upscaler is active. |
| `--smaa` | | off | Uses SMAA instead of FXAA when no temporal upscaler is active. |
| `--no-heat-haze` | | on | Disables heat haze distortion. |
| `--bloom <x>` | `0` to `0.25` | from preset | Bloom intensity (`0` disables bloom). |
| `--particle-divisor <0\|1\|2\|4>` | divisor | `0` | Renders weather particles at `1/n` resolution (`0` picks per sprite). Rain and fire stay full resolution. |
| `--gtao-quality <0\|1\|2>` | `0`, `1`, or `2` | `1` | Ambient occlusion quality. For `gtao`: `0` = half-res 2x4, `1` = half-res 3x6, `2` = full-res 3x6 ([post](render-post.md), "GTAO"). For SSAO: 8, 12, or 20 taps. |

### Meitou and Faithful switches

| Option | Value | Default | What it does |
|---|---|---|---|
| `--meitou <ids>` | comma list or `all` | `all` | Enables the listed Meitou visual enhancements. |
| `--faithful <ids>` | comma list or `all` | | Disables the listed enhancements to match original Kenshi's look. |

Available switch IDs (`Enhancements.cs`): `ao`, `contact`, `dither`, `haze`, `aa`, `shadows`, `range`, `impostors`, `reach`, `water`, `particles`, `lod`, `gi`, `tonemap`, `shafts`, `heathaze`, `night`, `planetshine`, `stars`, `nightgrade`, `clouds`, `cloudshadows`, `cloudfog`, `interiorgi`, `charlod`, `skin`, `interiorlamps`, `icons`, `blood`, `bloodsurfaces`, `footprints`, `parallax`, `displace`, `weld`, `tracers`, `dustlight`, `fog`, `gihybrid`, `chars2` (off by default), `trails`, `contactfoam`, `dof`, `gtao` (off by default; requires `ao`; enabled by `--meitou all`), `lampshadows` (off by default, not in any preset), `ssr` (off by default), `traced`, `lampwarmth`, `glass`, `exposure`, `highclouds` (off by default, not enabled by `--meitou all`). Each switch's strength can also be adjusted from the Tab overlay.

### Offscreen benchmarks and A/B comparisons (`--screenshot`)

| Option | Value | Default | What it does |
|---|---|---|---|
| `--ab <id>` | switch ID | | Alternates between Meitou (A) and Faithful (B) for one enhancement switch. |
| `--ab-period <k>` | frames | `1` | Frames shown per side during `--ab`. |
| `--bench-frames <n>` | frames | off | Renders `n` offscreen frames after warmup and writes per-pass timings to JSON. |
| `--bench-out <file>` | file path | temp folder | Destination path for benchmark JSON output. |
| `--bench-motion <m>` | `still`, `orbit`, `fly`, `turn` | `still` | Camera movement mode during benchmarking. |
| `--bench-orbit <px>` | pixels/frame | | Orbit speed when `--bench-motion orbit` is used. |
| `--bench-serial` | | off | Waits for GPU completion every frame (measures CPU + GPU together). |
| `--bench-tris` | | off | With `--bench-frames`: counts triangles rendered per pass. |
| `--bench-compare <a.json> <b.json>` | two JSON files | | Prints a side-by-side comparison of two benchmark JSON files and exits. |
| `--no-gpu-lock` | | off | Skips waiting on `%TEMP%\meitou-gpu.lock`. |
| `--fly-benchmark <frames>` | frames | off | Flies the camera in a circle offscreen and prints frame-time percentiles. |
| `--fly-radius <u>` | units | `12000` | Flight circle radius. |
| `--fly-speed <u/frame>` | units/frame | `150` | Flight speed. |
| `--fly-pipelined` | | off | Keeps two frames in flight without stalling the GPU every frame. |
| `--fly-csv <file>` | file path | off | Writes per-frame timings and VRAM usage to CSV, plus allocator breakdowns every 30 frames in `<file>.owners.csv`. |

## Meitou.Tools (`meitou-tools`)

Parsed in `tools/Meitou.Tools/Program.cs`, `NavmeshOptions.cs`, `WeatherTool.cs`, and `AudioTool.cs`. Every subcommand except `image-diff` and `world-map` reads your Kenshi installation.

| Subcommand | Arguments | What it does |
|---|---|---|
| `formats` | | Counts file types inside the install's `data` folder. |
| `fcs <file>` | path or name in `data` | Summarizes a `.mod` or `.base` file. |
| `load` | | Applies the active load order and summarizes merged game data. |
| `armour <name>` | armour name | Lists matching `ARMOUR` records after load-order merging alongside quicksave copies. |
| `meshes` / `skeletons` / `materials` | | Parses and reports on every mesh, skeleton, or material script. |
| `fcs-types` | | Maps numeric FCS record types to `fcs.def` names. |
| `fcs-records <N>` | type number | Prints fields for the first records of type `N`. |
| `world` | | Cross-checks the heightmap, zone files, and `features.dat`. |
| `particles [effects]` | optional `effects` | Surveys particle scripts (`effects` also lists `EFFECT` records). |
| `particles weathers` | | Lists all fog volumes and `WEATHER` records with their effects, fog, and lights. |
| `particles warmup [weather...]` | weather names | Measures the cost of transitioning into the named weathers. |
| `navmesh [...]` | see below | Builds and inspects zone navmeshes and writes debug exports. |
| `weather [...]` | see below | Simulates season and weather timelines for a region. |
| `audio [bank] [...]` | see below | Surveys Wwise sound banks, dumps bank info, or decodes WAVs. |
| `image-diff <a.png> <b.png> [diff.png]` | | Reports mean pixel difference and percentage of pixels differing by more than 12/255. |
| `world-map <png> [step]` | `step` (default `16`) | Generates a top-down world map PNG. |

### `navmesh` options

- **Target area:** `--zone x,z`, `--town name`, `--around n` (include `n` surrounding zones), `--box x0,z0,x1,z1`, `--base` (base-game records and levels only, ignoring enabled mods).
- **Exports & output:** `--geometry`, `--obj f.obj`, `--png f.png`, `--scale u`, `--repeat n`, `--fingerprint` (SHA-256 of cache files), `--verbose` (extra details on failed paths and interior faces).
- **Path & collision checks:** `--path x0,z0,x1,z1` (with `--from-y y`, `--to-y y`, `--closed`), `--route x0,z0,x1,z1` (long-range route with building footprints and map features), `--near x,z,radius` (buildings, features, carvers, and solids near a point), `--probe x,z[;x,z...]` (vertical column breakdown: triangles, solids, and both navmeshes), `--probe x,z,radius,step` (walkable-ground agreement map), `--feature-tops` (walkable tops per map feature), `--regions` (compares regions, extra islands, and overrun bridges within `--box`), `--ragdoll` (zone ragdoll collision triangles), `--compare x,z ...`.
- **Interiors:** `--no-interiors`, `--interior key` (parts, components, and coverage for one building), `--own-only` (building's own shapes without furniture or neighbors), `--compare-interiors x,z ... | towns` (compares interior coverage and street connectivity against original tiles, flagging `SEALED` rooms and buildings under 90%), `--survey-interiors zones|towns` (area comparison summed by building type, with `--name text`, `--png dir`, `--section x,z`, and `--border`).
- **Build settings (`NavBuildSettings`):** `--cell u`, `--cell-height u`, `--climb u`, `--tile n` (cells per tile), `--min-region n`, `--merge-region n`, `--simplify e` (contour error in cells), `--edge n` (max contour edge), `--monotone` (use monotone regions instead of watershed).

### `weather` options

`--region name`, `--at x,z`, `--days d0 d1`, `--seed n`, `--list` (list regions and calendars), `--extents`.

### `audio` options

`--media`, `--decode`, `--wav <folder> [wem id]`.

## Launcher (`launcher/Meitou.Launcher`)

The launcher accepts no command-line options of its own. When launching the game, it passes:

- the user's launch arguments from its settings (`LauncherConfig`), split on spaces; the Graphics page warns that `--low-end` or `--benchmark-baseline` makes the game ignore the graphics sliders;
- `--renderer <vulkan|metal>`, appended after user arguments, so it wins over a hand-written one (`GameArguments.cs`).

The Coop Play page selects an installed release or explicit local client in both Debug and Release. **Connect** appends
`--renderer vulkan --connect <host:port>` after user arguments, rejecting duplicate connection and local-world options.
See [launcher](launcher.md#coop-client-selection-and-connections).

Environment it sets for the game: `KENSHI_PATH`; on Windows with `steam_api64.dll` and **Launch as Kenshi on Steam** on, `SteamAppId` and `SteamGameId` (and a temporary `steam_appid.txt`); on macOS `MEITOU_METAL_GPU_BUDGET_MB` (Metal, unless the budget is Auto) or the Vulkan loader path. It removes any inherited `MEITOU_METAL_GPU_CULL` and `MEITOU_METAL_GPU_BUDGET_MB` first. It reads `DYLD_LIBRARY_PATH` on macOS (`MacSupport.cs`). The game inherits `MEITOU_IDENTITY_DIRECTORY` unchanged.

## Development executables

Internal tools with no separate user docs:

- `tools/Meitou.MetalHost/Program.cs` (Metal sky backend test harness): `--hour <h>` (default `12`), `--hold` (1800 frames instead of 300), `--shot <png>` (saves after 10 frames), `--world` (uses the install's sky), `--tri` (triangle test), `--table` (bindless table triangle test; implies `--tri`).

## Environment variables

| Variable | Read by (file) | Effect |
|---|---|---|
| `MEITOU_IDENTITY_DIRECTORY` | `src/Meitou.Networking/DeviceConnection.cs` | Directory for per-server device credentials, for both menu and CLI address connections; blank/unset uses the existing default. Legacy JSON profiles are unaffected. Packaged `start-client-player1.cmd` uses the default; `start-client-player2.cmd` uses `%LOCALAPPDATA%/Meitou/identities-player2`. |
| `KENSHI_PATH` | `src/Meitou.Core/Content/GameInstall.cs`, `tools/scripts/*.ps1` | Kenshi install folder. Also used by the test suite through `GameInstall.Locate`; tests skip when it is missing. |
| `MEITOU_LOG_TIMES` | `src/Meitou.Game/Host/GameLog.cs` | `1`: every log line starts with the seconds since the process started (to time a start or a load). |
| `MEITOU_MODS_CFG` | `src/Meitou.Game/Host/Program.cs` | Same as `--mods-cfg <file>`; the option wins. |
| `MEITOU_CONTROL_DIR` | `src/Meitou.Game/DevTools/Agents/AgentChannel.cs` | Same as `--control-dir <dir>`; the option wins. |
| `MEITOU_HEADLESS` | `src/Meitou.Game/Rendering/Frame/WorldCommandLine.cs` | `1`: same as `--headless`. |
| `DYLD_LIBRARY_PATH` | `src/Meitou.Game/Rendering/Gpu/Core/VulkanDevice.cs`, `launcher/Meitou.Launcher/MacSupport.cs` | macOS Vulkan loader search path; only named in the error message the viewer prints. The launcher sets it. |
| `SteamAppId` | `src/Meitou.Game/Host/SteamSession.cs` | Steam app id; the launcher sets it on Windows. |
| `SteamGameId` | launcher only | Set for Steam's overlay; not read by project code. |
| `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_DATA_DIRS` | `src/Meitou.Core/Content/GameInstallSources.cs` | Linux and macOS install discovery and the `meitou/config.json` location. |
| `MTL_CAPTURE_ENABLED` | Metal framework (not project code) | Enables Metal GPU capture; the game's Ctrl+/ key uses it (per the usage text). |
| `MEITOU_PASS_STATS` | `tools/Meitou.ModelViewer/PassMeter.cs`, `src/Meitou.Game/Rendering/Gpu/FrameLoop.cs` | `1`: per-pass GPU cost breakdown. |
| `MEITOU_REMOTE_ACCEPTANCE` | `src/Meitou.Game/GameHost.NetworkAcceptance.cs` | `1`: isolated-copy online acceptance driver; selects `--role`, warms 35 s, submits short stationary movement orders and logs numeric 10 s transport/frame samples. Uses real input/network/rendering; fails on disconnection. |
| `MEITOU_REMOTE_ACCEPTANCE_PICTURES` | `GameHost.NetworkAcceptance.cs` | Optional output folder for 12 actual GPU frames. Use a separate visual run: synchronous PNG capture contaminates latency measurements. |
| `MEITOU_PASS_STATS_SKIP` | `tools/Meitou.ModelViewer/PassMeter.cs` | Frames skipped before the breakdown (default 60). |
| `MEITOU_BENCH_ORBIT` | `tools/Meitou.ModelViewer/FlyBenchmarkSession.cs` | Pixels per frame the benchmark camera orbits (0.005 rad per pixel). |
| `MEITOU_BENCH_SKIP` | `FlyBenchmarkSession.cs` (default 1), `src/Meitou.Game/Rendering/Profiling/SpikeLog.cs` (default 0) | Frames left out of benchmark percentiles and spike logs. |
| `MEITOU_FLY_TURN` | `FlyBenchmarkSession.cs` | Radians the fly benchmark turns each frame instead of flying. |
| `MEITOU_STREAM_LOG` | `tools/Meitou.ModelViewer/WorldApp.cs` | `1`: print frames slower than 15 ms with streamer timings. |
| `MEITOU_VRAM_KILL` | `tools/Meitou.ModelViewer/VramWatch.cs` | Benchmark exits (code 9) when video memory passes this fraction of the budget (default 0.95; 0 off). |
| `MEITOU_LOD_DEBUG` | `src/Meitou.Game/Rendering/Objects/WorldObjectRenderer.cs` | `1`: colour surfaces by LOD level; `2`: wireframe coloured by level. |
| `MEITOU_FOLIAGE_RANGE` | `src/Meitou.Game/Rendering/Foliage/FoliageRenderer.cs` | Starting value of the foliage range setting (offscreen measurements). |
| `MEITOU_GRASS_RANGE` | `src/Meitou.Game/Rendering/Foliage/FoliageRenderer.cs` | Starting value of the grass range setting. |
| `MEITOU_FORCE_INTEGRATED` | `src/Meitou.Game/Rendering/Gpu/Core/VulkanDevice.cs` | `1`: treat any GPU as integrated (budgets for integrated GPUs). |
| `MEITOU_VRAM_BUDGET_MB` | `src/Meitou.Game/Rendering/Gpu/Core/VulkanDevice.cs` | Caps the video memory reported, for testing the memory guard. |
| `MEITOU_VK_VALIDATION` | `src/Meitou.Game/Rendering/Gpu/Core/VulkanDeviceOptions.cs` | `1` validation; `sync` adds synchronisation checks; `gpu` adds GPU-assisted checks; `0` off. Unset: caller's default. |
| `MEITOU_DLSS_FAIL_AT` | `src/Meitou.Game/Rendering/Upscalers/DlssUpscaler.cs` | Test hook: `n` reports a DLSS failure after the n-th evaluation was recorded, to exercise the fallback to TAA. |
| `MEITOU_DIAG_SUBMIT` | `src/Meitou.Game/Rendering/Gpu/FrameLoop.cs`, `src/Meitou.Game/Host/GameLoop.cs` | `1`: diagnostic (issue #108): the world is submitted and waited for before the interface draws, and the interface before the present. Slower frames. |
| `MEITOU_DIAG_IDLE` | `src/Meitou.Game/Rendering/Gpu/FrameLoop.cs` | `1`: diagnostic (issue #108): each frame waits for every frame in flight before it begins. Slower frames. |
| `MEITOU_DIAG_NO_MATERIAL_CACHE` | `src/Meitou.Game/Rendering/Gpu/FrameLoop.cs` | `1`: diagnostic (issue #319): the terrain material cache starts off; every pixel shades the layers. |
| `MEITOU_DIAG_NO_GROW` | `src/Meitou.Game/Rendering/Gpu/FrameLoop.cs` | `1`: diagnostic (issue #319): the terrain near layer arrays and the grass store keep their first size (no growth for kept places or a longer grass distance). |
| `MEITOU_DIAG_FAULT` | `src/Meitou.Game/Rendering/Gpu/Core/DeviceFault.cs` | `1`: diagnostic (issue #319): GPU addresses (VK_EXT_device_address_binding_report) and per-pass markers are tracked, so the report a lost device writes names the object at the fault address and the pass the GPU reached; a character part drawn with a texture index freed in an earlier frame logs a `diagnostic #319:` line (once per material and index). |
| `MEITOU_VRAM_GUARD` | `src/Meitou.Game/Rendering/Frame/WorldGpuBuilder.cs`, `src/Meitou.Game/Host/MetalGameLoop.cs` | `0` turns the memory-pressure guard off. On by default. |
| `MEITOU_OBJECT_LOD_CACHE` | `src/Meitou.Game/Rendering/Objects/ObjectLodGen.cs` | Folder for the object LOD cache (default under `%LOCALAPPDATA%\Meitou`). |
| `MEITOU_CHARACTER_LOD_CACHE` | `src/Meitou.Game/Rendering/Characters/CharacterContent.cs` | Folder for the character LOD cache (default under `%LOCALAPPDATA%\Meitou`). |
| `MEITOU_IMPOSTOR_CACHE` | `src/Meitou.Game/Rendering/Impostors/ImpostorCache.cs` | Impostor cache folder. |
| `MEITOU_SSAO_STAMPS` | `src/Meitou.Game/Rendering/Post/AmbientOcclusion.cs` | `1`: GPU timestamps around the SSAO pass (diagnostic). |
| `MEITOU_SHAFT_STAMPS` | `src/Meitou.Game/Rendering/Post/LightShafts.cs` | `1`: GPU timestamps around the light-shaft pass (diagnostic). |
| `MEITOU_UPSCALER_LOG` | `src/Meitou.Game/Rendering/Upscalers/Streamline.cs`, `FsrUpscaler.cs` | `1`: log upscaler info (errors are always logged). |
| `MEITOU_DLSS_PATH` | `src/Meitou.Game/Rendering/Upscalers/VendorUpscalers.cs` | Folder of the DLSS runtime: the Streamline DLLs on Windows, the DLSS model and `libmeitou_ngx.so` on Linux. |
| `MEITOU_STREAMLINE_PATH` | `src/Meitou.Game/Rendering/Upscalers/VendorUpscalers.cs` | Older name of `MEITOU_DLSS_PATH`, read when that is unset. |
| `MEITOU_FFX_PATH` | `src/Meitou.Game/Rendering/Upscalers/FsrUpscaler.cs` | Path to the FidelityFX library (FSR). |
| `MEITOU_DLSS_PRESET` | `src/Meitou.Game/Rendering/Upscalers/VendorUpscalers.cs` | Overrides the DLSS preset number (the settings' DLSS preset otherwise). |
| `MEITOU_NGX_LOG` | `src/Meitou.Game/Rendering/Upscalers/Ngx.cs` | `1` prints NGX's whole log (DLSS on Linux). |
| `MEITOU_DLSS` | `src/Meitou.Game/Rendering/Upscalers/Ngx.cs` | `0` keeps NGX from loading (no DLSS on Linux; the game uses TAA). |
| `MEITOU_BENCH_ONLY` | `src/Meitou.Game/PlayBench` | Comma list of `--play-bench` segments to run (e.g. `settle,zoom`). |
| `MEITOU_TERRAIN_NO_NORMAL` | `src/Meitou.Game/Host/MetalGameLoop.cs` | `1`/`true`: terrain shader variant without normal mapping (A/B; fixed before the first draw). |
| `MEITOU_METAL_GPU_BUDGET_MB` | `src/Meitou.Game/Host/MetalGameLoop.cs` | Metal video-memory budget in MB (default: a third of the Mac's memory in 256 MB steps, 5120 to 10240, never above the device's recommended working set: `MetalMemoryBudget`). The launcher sets it on macOS unless its budget is Auto; `--metal-gpu-budget` wins over it. |
| `MEITOU_METAL_NO_TAA` | `Host/MetalGameLoop.cs` | `1`/`true`: the upscaler Disabled (no jitter, no temporal resolve; diagnostic); in full screen the scene keeps the saved render resolution. |
| `MEITOU_METALFX` | `Host/MetalGameLoop.cs` | `1`/`true`: MetalFX upscaler (same as `--upscaler metalfx`). |
| `MEITOU_METALFX_SPATIAL` | `Host/MetalGameLoop.cs` | `1`/`true`: MetalFX spatial mode. |
| `MEITOU_METAL_FOLIAGE` | `Host/MetalGameLoop.cs` | `0`: foliage off on Metal (on by default). |
| `MEITOU_METAL_CLOUD_SHADOWS` | `Host/MetalGameLoop.cs` | `0`: cloud shadows off on Metal (on by default). |
| `MEITOU_METAL_WATER` | `Host/MetalGameLoop.cs` | `0`: water off on Metal (on by default). |
| `MEITOU_METAL_SHADOWS` | `Host/MetalGameLoop.cs` | `0`: shadows off on Metal (on by default). |
| `MEITOU_METAL_NO_IMPOSTORS` | `Host/MetalGameLoop.cs` | `1`: no impostors on Metal. |
| `MEITOU_METAL_NO_TERRAIN_TEXTURES` | `Host/MetalGameLoop.cs` | `1`: terrain textures off (prints a notice). |
| `MEITOU_METAL_NO_PARTICLES` | `Host/MetalGameLoop.cs` | `1`: no weather particles on Metal. |
| `MEITOU_METAL_GROUND_RELIEF` | `Host/MetalGameLoop.cs` | `0` or `false`: ground relief (parallax) off; on by default. |
| `MEITOU_METAL_GI` | `Host/MetalGameLoop.cs` | `1`/`true`: native Metal probe GI (opt-in; experimental). |
| `MEITOU_METAL_GI_LOG` | `src/Meitou.Game/Rendering/Metal/Gi/MetalProbeGi.cs` | `1`: log the probe GI. |
| `MEITOU_METAL_GI_FALLBACK` | `src/Meitou.Game/Rendering/Metal/Gi/MetalProbeGi.cs` | `1`: disables the sky-light term of the probe GI (A/B). |
| `MEITOU_METAL_NO_GRASS` | `Host/MetalGameLoop.cs` | `1`: no grass blades on Metal. |
| `MEITOU_METAL_NO_FOLIAGE_MESHES` | `Host/MetalGameLoop.cs` | `1`: no foliage meshes on Metal (grass only). |
| `MEITOU_METAL_GPU_CULL` | `Host/MetalGameLoop.cs`, `src/Meitou.Game/Rendering/Metal/MetalFrame.cs`, `src/Meitou.Game/Rendering/Foliage/FoliageRenderer.cs` | `1`/`true`: experimental GPU foliage cull (unsafe; the launcher removes it). |
| `MEITOU_METAL_CPU_CULL` | `Host/MetalGameLoop.cs` | `1`: force the CPU foliage cull, overriding the GPU cull. |
| `MEITOU_METAL_TRACE` | `Host/MetalGameLoop.cs`, `Rendering/Metal/MetalPipelines.cs`, `MetalStage.cs` | `1`: trace output of the Metal frame loop, and each pipeline state and MSL library with its build time. |
| `MEITOU_METAL_NO_BUILDINGS` | `Host/MetalGameLoop.cs` | `1`: skip the runtime building, ground-item and lamp updates (freeze diagnostic). |
| `MEITOU_METAL_NO_PARTICLE_COVERAGE` | `src/Meitou.Game/Rendering/Metal/MetalPostChain.cs` | `1`/`true`: particles do not write TAA's coverage alpha. |
| `MEITOU_METAL_OVERLAY` | `Host/MetalGameLoop.cs` | `1`: add the frame-stats lines to a screenshot. |
| `MEITOU_METAL_SETTINGS` | `Host/MetalGameLoop.cs` | `1`/`true`: open the settings panel for a still. |
| `MEITOU_METAL_FAST_MATH` | `src/Meitou.Game/Rendering/Metal/MetalShaders.cs` | Metal fast-math compile option: unset is Metal's default (on); `1` or `0` forces it. |
| `MEITOU_METAL_DUMP` | `src/Meitou.Game/Rendering/Metal/MetalShaders.cs` | Folder to write the translated MSL of each shader into. |
| `MEITOU_METAL_NO_COMPRESSION` | `src/Meitou.Game/Rendering/Metal/MetalContext.cs` | `1`: no lossless target compression (A/B). |
| `MEITOU_METAL_SLOT_WAIT` | `src/Meitou.Game/Rendering/Metal/MetalContext.cs` | Unset or `1`: wait for the reused slot's fence (default). `0`/`false`: frame-number only. `serial`: one frame in flight. |
| `MEITOU_METAL_NO_STATE_CACHE` | `src/Meitou.Game/Rendering/Metal/MetalCommandList.cs` | `1`: reissue every encoder state (A/B). |
| `MEITOU_METAL_SHAFT_SCAN` | `src/Meitou.Game/Rendering/Post/PostComposite.cs` | `0`: the light shafts' integrate and scatter as the two fragment passes instead of the compute scan (A/B). |
| `MEITOU_METAL_PRIVATE_STATIC` | `src/Meitou.Game/Rendering/Terrain/TerrainRenderer.cs`, `TerrainTextures.cs` | `1`: private storage for static terrain data (A/B). |

Names starting `MEITOU_` are also recorded in the benchmark JSON (`BenchSession.cs`).

## Unclear

- `MEITOU_BENCH_SKIP` defaults differ: 1 in `FlyBenchmarkSession.Report`, 0 in `SpikeLog.cs:19`.
- `MEITOU_METAL_GI_FALLBACK`, `MEITOU_METAL_PRIVATE_STATIC` and the two `*_STAMPS` variables have the upstream-described effects listed above; their runtime effect has not been independently verified in this checkout.
- `MEITOU_FRAGMENT` (`src/Meitou.Game/Rendering/Gpu/Shaders/GlslProgramCompiler.cs:167`) is a shader macro, not an environment variable, so it is excluded.
- `MTL_CAPTURE_ENABLED` appears only in the game's usage text (`GameOptions.cs:120`); no project code reads it.
- The viewer's `--class` and the impostor `--debug` semantics are not traced beyond the parse.

**Observed** (implementation): `--benchmark-baseline` uses Medium quality, disables GI and ground relief, and selects full-resolution SMAA for this run. `--ui-panel <name>` selects one screenshot panel. `--hidden-window` renders without showing the window.
