# rimworld-mod-mcp

[![M8ven Score](https://m8ven.ai/badge/mcp/mushroomtw/rimworld-mod-mcp)](https://m8ven.ai/mcp/mushroomtw/rimworld-mod-mcp)
[![MCP](https://img.shields.io/badge/MCP-Model_Context_Protocol-blue)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A local-only [Model Context Protocol](https://modelcontextprotocol.io/) server that lets Claude Code, Codex, and other MCP clients research RimWorld's API and Defs, create and build mods, validate assets, and test them in an isolated game session.

This project is inspired by [Modmixer](https://github.com/lebek/modmixer) but is an independent implementation: it contains no Modmixer source code, does not depend on Modmixer, and never communicates with Modmixer or any other external service.

> This project contains **no Steam Workshop publishing, uploading, or any external content transfer**.

[繁體中文](README_zh.md)

## Highlights

- Runs on Windows, macOS, and Linux; ships as a single self-contained executable (or a dotnet tool built from source).
- **Reads IL metadata directly to build the symbol index** — roughly a hundred thousand symbols in seconds, with complete inheritance chains and real type signatures.
- SQLite FTS5 full-text search over Defs, symbols, and decompiled source.
- In-process decompilation via `ICSharpCode.Decompiler`. No external tools, no toolchain setup step.
- Tests mods with an isolated save folder and temporary directory links, leaving your real saves and settings untouched.
- Merges diagnostics from the in-game bridge and `Player.log`. If the bridge cannot connect, `Player.log` still works and the reason is reported rather than silently degraded.
- The bridge also reports **in-game state** (main menu vs. playing, map loaded, tick, paused, loading, open dialogs), so an agent can wait for the map to come up before judging a test instead of guessing from log silence.
- The bridge ships as source and is built by `run_test_cycle` against your installed game's own assemblies.
- Game files, decompiled output, indexes, and diagnostics all stay on your machine.

## Requirements

- A legitimately installed copy of RimWorld 1.7 (RimWorld 1.6 is not supported)
- [.NET SDK 10](https://dotnet.microsoft.com/download), for `run_test_cycle` (building the bridge) or for installing from source

> [!NOTE]
> The release executable is self-contained: running the server needs no .NET Runtime. The in-game bridge is embedded in the tool as source; `run_test_cycle` compiles it once against the RimWorld assemblies it detects (which needs the SDK) and reuses that build until the bridge or the game changes. This repository itself contains no RimWorld binaries.

RimWorld 1.7 no longer runs on Unity: it runs on .NET 10 with its assemblies in the install root. The in-game bridge builds against those local assemblies; your own C# mods target `net10.0` (for example through the `Krafs.Rimworld.Ref` NuGet package).

## Installation

### Ask your agent to install it

If you use Claude Code, Codex, or another coding agent, you can hand the whole setup over. Paste this prompt:

```text
Please install rimworld-mod-mcp for me, following https://github.com/mushroomTW/rimworld-mod-mcp (README.md).
Download the executable for my platform from the latest release, put it in a stable folder, and register it
as an MCP server named "rimworld" with the argument "stdio" in my MCP client. Then call rimworld_status
to confirm RimWorld was detected.
```

The agent downloads the file, registers it in your client's configuration, and verifies it. Restart or reload your MCP client afterwards if the new server does not appear.

### Download a release (recommended)

Download the executable for your platform from the [latest release](https://github.com/mushroomTW/rimworld-mod-mcp/releases/latest):

| Platform | File |
| --- | --- |
| Windows x64 | `rimworld-mod-mcp-<version>-win-x64.exe` |
| macOS (Apple Silicon) | `rimworld-mod-mcp-<version>-osx-arm64` |
| Linux x64 | `rimworld-mod-mcp-<version>-linux-x64` |

That single file is the whole tool: no .NET Runtime is needed, and the in-game bridge is embedded in it (it is unpacked to the cache directory the first time `run_test_cycle` needs it). Put it anywhere and point your MCP client at it.

On macOS and Linux, mark the file as executable after downloading. macOS may also block it because it is not notarized; clear the quarantine flag once:

```bash
chmod +x rimworld-mod-mcp-*-osx-arm64
xattr -d com.apple.quarantine rimworld-mod-mcp-*-osx-arm64
```

### From source

As a global dotnet tool:

```bash
dotnet pack src/RimWorldModMcp.Server -c Release -o ./nupkg
dotnet tool install -g RimWorldModMcp --add-source ./nupkg
```

Or as a self-contained single executable, the same way the releases are built:

```bash
# Windows (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# macOS (Apple Silicon)
dotnet publish src/RimWorldModMcp.Server -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# Linux (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

> **Note**: The publish output also contains a `bridge/` directory. The executable does not need it (the bridge is embedded), but when it is present next to the executable it takes precedence over the embedded copy.

## MCP client configuration

Point the command at the executable you downloaded — see [local-build.mcp.json](examples/local-build.mcp.json):

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "C:\\path\\to\\rimworld-mod-mcp-<version>-win-x64.exe",
      "args": ["stdio"]
    }
  }
}
```

On macOS and Linux, use the absolute path to the downloaded file instead. When installed as a dotnet tool from source, `rimworld-mod-mcp` is on your PATH, so no absolute path is needed:

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "rimworld-mod-mcp",
      "args": ["stdio"]
    }
  }
}
```

`run_test_cycle` accepts any local mod directory path; there is no write boundary — scoping writes is left to your MCP client's permission prompts and to you.

Templates live in [examples/](examples/): [Claude Code](examples/claude-code.mcp.json), [Codex](examples/codex-mcp.toml), [custom port and game path](examples/custom-port.mcp.json).

## First use

1. Configure the server in your MCP client.
2. Call `rimworld_status` to confirm RimWorld was detected.
3. Start querying. The first query tool call builds the index automatically (Def and symbol indexing takes seconds), and the server rebuilds it the same way whenever the game is updated or the index database is damaged — there is no rebuild tool to call. The source-code full-text index continues in the background, and `search_source` reports `source_indexed: false` until it completes.

## Tools

### Researching the game

| Tool | Purpose |
| --- | --- |
| `rimworld_status` | Detected paths and index state. `index.source_index` says whether the source full-text layer is running, failed (`error`), or done. |
| `search_defs` | Search Defs by name, label, or description. |
| `read_def` | Read one Def's full XML. Look up abstract Defs by their `Name` attribute. |
| `read_symbol` | Signature, full inheritance chain, and interfaces for a symbol; optionally decompiled source. Exact name matches win: when any exist, substring matches are left out and counted in `partial_count`. `assembly` narrows to one assembly (or one version of a multi-version mod). Zero hits return `suggestions`. |
| `list_symbols` | Browse without a keyword: a namespace lists its top-level types and child namespaces; a type lists its members. An unknown parent is an error, not an empty list. |
| `find_descendants` | Find every class deriving from a given type; a short name works when unambiguous. Zero hits return `suggestions`. |
| `search_source` | Regex search (.NET syntax, always case-insensitive) over decompiled source: the game by default, or one installed mod with `package_id` (first use decompiles in the background; `indexing=true` means retry shortly). `assembly` (e.g. `1.7/`) restricts a mod search to one version's DLL. Prefer simple literals (`CurTimeSpeed`); `a|b` matches either branch. |
| `read_source_file` | Read a whole decompiled file (game or mod) by the `assembly` and `file` a search returned, paging by line (`start_line` + `max_bytes`). Check `source_indexed` before trusting empty results. |
| `find_def_usages` | Find where a Def is referenced: XML cross-references, plus C# code that uses it through `XxxDefOf.<defName>` (searched in decompiled source, so `indexing=true` means retry shortly). Covers the game by default, or one installed mod with `package_id` (its Def XML is scanned on demand). Use it to verify translatable fields before hand-writing `Languages/` XML. |

### Installed mods

| Tool | Purpose |
| --- | --- |
| `list_installed_mods` | List local and Workshop mods. Filter with `package_id` and page with `limit`/`offset`; `include_details` adds the path, dependencies and versions. |

`search_source`, `find_def_usages`, and the symbol tools decompile and index an installed mod in the background the first time you scope them to it. Each assembly is keyed `mod:<packageId>:<relative DLL path>`, e.g. `mod:cj.rimtalk:1.7/Assemblies/RimTalk.dll`.

The tool set deliberately covers only what an AI coding agent cannot do on its own: querying Defs and decompiled source from the game and other mods, and launching the game in an isolated test session. Creating files, building with `dotnet build`, and snapshots are things the agent already does natively, so no tools are provided for them — use Git for version control.

### Testing and diagnostics

| Tool | Purpose |
| --- | --- |
| `run_test_cycle` | Launch RimWorld in an isolated session to test a mod. Use it for in-game acceptance (time-speed feel, rendering) that static analysis cannot cover. |
| `test_status` | Current session state: whether the bridge and daemon are healthy, and `game`, the in-game state the bridge last reported (`program_state` Entry/MapInitializing/Playing, `map_loaded`, `tick`, `paused`, `time_speed`, `loading`, `open_windows`, `colonists`, `age_ms`). Pass `wait_for_state=Playing` to block until the map is up. |
| `stop_test` | Stop the session and clean up; requires `confirm: true`. |
| `list_test_diagnostics` | List collected errors and warnings. Poll with `since_at` (the previous `latest_at`) and `wait_seconds` to long-poll for new entries instead of re-reading the list. Raise `max_text_length` for a full stack trace. |

## The three indexing tiers

Indexing is deliberately layered so the most useful data is ready first:

| Tier | Contents | Cost |
| --- | --- | --- |
| 1 | Def XML and IL metadata symbols | Seconds; rebuilt automatically when stale |
| 2 | Single-member decompilation | Milliseconds, computed on demand by `read_symbol` |
| 3 | Full-assembly decompiled source, full-text indexed | Minutes, runs in the background |

Once tier 1 completes, Def search, symbol lookup, inheritance chains, and `read_symbol` all work. Only `search_source` waits on tier 3.

## How test sessions stay isolated

- `-savedatafolder` points at a temporary directory, so your real saves and settings are untouched.
- A generated `ModsConfig.xml` activates only the mod under test and its dependencies, carrying over your existing game version and known DLC.
- Temporary links in the Mods folder point at your workspace, so edits take effect without copying files. Windows uses junctions (no administrator rights needed); macOS and Linux use symbolic links.
- By default `run_test_cycle` adds `-NoWindow -NoCrashDialog` (RimWorld 1.7): the game window stays hidden but the game still renders, so screenshots work. The bridge still reports state and errors. Pass `no_window=false` to show the window.
- Starting a test is refused while the game is already running — it would interfere with your own save.
- Stopping a session removes the links, terminates the daemon, and cleans up the temporary save data. **Link removal never recurses into the target, so your mod source is never touched.**

### What the agent can see during a test

The bridge is a small Harmony mod, active only when a session token is present. It pushes two kinds of data over loopback using .NET 10's built-in `System.Text.Json` (`Utf8JsonWriter`) with thread-static buffers for zero-allocation streaming without external DLL dependencies:

- **Diagnostics**: every `Log.Error` / `Log.Warning`, deduplicated with occurrence counts; when the diagnostics daemon is unavailable, they fall back to `Player.log`.
- **Game state** (about once a second, or every 5 s as a heartbeat): `program_state`, whether a map is loaded, tick, paused, time speed, whether a long load is running, the open window types (a `Dialog_*` here usually means something is blocking), colonist count, and game version. `test_status` returns the latest report with its age.

No screenshots: coding agents typically have their own screen-capture tooling, and this server stays focused on what they cannot do alone.

## Compared with similar projects

There are several AI-modding tools for RimWorld. They mostly occupy different niches; this table is from each project's README and repository as of September 2026 and only lists what those sources state.

| | rimworld-mod-mcp | [Modmixer](https://github.com/lebek/modmixer) | [RimSage](https://github.com/realloon/RimSage) | [RimBridgeServer](https://github.com/pardeike/RimBridgeServer) | [RiMCP_hybrid](https://github.com/h7lu/RiMCP_hybrid) |
| --- | --- | --- | --- | --- | --- |
| Form | MCP server (single dotnet tool / executable), bring your own agent | Electron desktop app with a built-in agent; you supply a model API key | MCP server (Bun + ripgrep); hosted at mcp.rimsage.com or self-hosted | In-game mod exposing a tool bridge; used through [GABS](https://github.com/pardeike/GABS) or a direct connection | MCP server (C#, Lucene + vector + graph RAG) |
| Game API research | IL metadata symbol index in seconds; per-member decompilation on demand; full-text source in the background | Decompiles game and DLC assemblies with a vendored `ilspycmd`, then indexes | Yes, but you decompile the game yourself first (`import-csharp` takes a decompiled source tree) | No | Yes, but you decompile the game yourself first (ILSpy or dnSpy output placed in `RimWorldData/`); requires an embedding model (local or remote API) to build the index |
| Def XML search | Yes, with cross-reference lookup (`find_def_usages`) | Yes | Yes (`search_defs`, `get_def_details`) | No | Yes |
| Installed mods | List, decompile and search other mods' assemblies | Lists local and Workshop mods, edits the active list and load order, flags missing dependencies, incompatibilities and load-order problems; indexes only game/DLC assemblies, decompiles other DLLs on demand (`decompile_dll`) | No | List mods, read/change mod settings and load order in the running game | No |
| Build | No (the agent runs `dotnet build` itself) | Yes (agent edits and builds) | No | No | No |
| Test launch | Isolated save folder, generated `ModsConfig.xml`, junction/symlink to your workspace | Launches the game with the mod installed; bridge mod watches for errors | No | Starts a debug game or loads a save through GABS | No |
| In-game feedback | Errors/warnings + game state (scene, map, tick, paused, open dialogs) | Errors via its bridge mod | No | Full live state, semantic UI layout, screenshots, debug actions, Lua scripting | No |
| Steam Workshop | None by design | Publishing built in | No | No | No |
| Network | Local (loopback only; the first bridge build may restore its NuGet packages) | Model provider API, Workshop upload; ships `@sentry/electron` | Hosted mode sends queries to rimsage.com; self-hosted mode is local | Local | Local unless a remote embedding API is used |
| License | MIT | MIT | MIT | MIT | MIT |

Where this project fits:

- **Versus Modmixer**: same overall loop (research → build → launch → read errors), but as a headless MCP server for coding agents you already use (Claude Code, Codex, …) rather than a desktop app with its own chat. No Workshop publishing and no art/audio pipeline. Indexing is layered so symbol lookup works within seconds of the first query instead of waiting for a full decompile.
- **Versus RimSage / RiMCP_hybrid**: those are research-only. This server also decompiles for you and tests; RiMCP_hybrid's semantic search may find conceptually related code that a literal/regex search misses.
- **Versus RimBridgeServer**: complementary rather than competing. RimBridgeServer gives an agent deep control of a running game (UI, screenshots, debug actions); this project's bridge reports only diagnostics and coarse game state. Use RimBridgeServer when you need in-game interaction; use this server for the API research, build, and isolated launch around it.

## Security and privacy

- The only tool that writes outside the server's own index and data directories is `run_test_cycle`: it links the mod directory you pass into the game's `Mods` folder and uses an isolated temporary save directory. There is no path allow-list; rely on your MCP client's tool-permission prompts.
- No external network communication by the server itself (the first bridge build may restore its packages from NuGet). Game files, decompiled output, indexes, and diagnostics stay local.
- The bridge activates only when `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` is present and connects only to `127.0.0.1`. Each test session uses a single-use token; the token is never written to diagnostic records and never appears in tool responses.
- Destructive operations (`stop_test`) require an explicit `confirm: true`.

## Environment variables

| Variable | Purpose |
| --- | --- |
| `RIMWORLD_MOD_MCP_GAME_PATH` | Override RimWorld installation detection. |
| `RIMWORLD_MOD_MCP_PLAYER_LOG` | Override the `Player.log` location. |
| `RIMWORLD_MOD_MCP_BRIDGE_PORT` | Loopback port for the diagnostics daemon; defaults to 49460. |
| `RIMWORLD_MOD_MCP_PERF_MARKERS` | Extra markers, separated by `\|`. A `Log.Message` containing `[perf]` or any extra marker is recorded as a `performance` diagnostic (query with `list_test_diagnostics(type=performance)`). |
| `RIMWORLD_MANAGED_DIR` | Directory containing game assemblies (`Assembly-CSharp.dll` and `UnityStubs.dll`). Injected automatically for bridge builds and legacy mod projects; can be set manually for command-line builds. |

## Development

```bash
dotnet build RimWorldModMcp.slnx
dotnet test RimWorldModMcp.slnx
```

CI runs the platform contract tests on Windows, macOS, and Ubuntu — junction behaviour and process liveness semantics differ per platform, so verifying on only one would lose the protection they provide.

Development subcommand (not part of the tool listing, useful for troubleshooting):

```bash
dotnet run --project src/RimWorldModMcp.Server -- detect      # show detected paths
```

## License

MIT, see [LICENSE](LICENSE).
