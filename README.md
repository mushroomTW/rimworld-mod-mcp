# rimworld-mcp

`rimworld-mcp` is a local-only, cross-platform [Model Context Protocol](https://modelcontextprotocol.io/)
server for RimWorld mod development. It gives Codex, Claude Code, and other MCP clients
tools for researching RimWorld APIs and Defs, creating and building mods, validating assets,
and running isolated game test sessions.

Its design and architecture were informed by [Modmixer](https://github.com/lebek/modmixer), but this
is an independent implementation: it contains no Modmixer source code, does not depend on Modmixer,
and never communicates with Modmixer or any other external service.

> This project does **not** publish to Steam Workshop or upload any content.

## Highlights

- Supports Windows, macOS, and Linux with CPython 3.14+.
- Uses the official Python MCP SDK 2.x and an stdio server (`MCPServer`).
- Detects local RimWorld installations, Steam Workshop content, configuration files, and
  `Player.log`; game and log paths can be overridden with environment variables.
- Decompiles local Core and installed DLC assemblies through project-local ILSpyCmd, then builds
  a SQLite FTS5 Def index and C# symbol/source index.
- Uses Tree-sitter C# when the optional binding is installed and falls back safely to a regex
  parser when it is unavailable.
- Supports on-demand, local-only decompilation and source search for installed third-party mods.
- Creates XML-only or C# mod skeletons, validates metadata and assets, and deploys successful C#
  build output to the mod's `Assemblies/` directory.
- Runs tests with an isolated `-savedatafolder`, a per-run Bridge token, and owned junctions
  (Windows) or symbolic links (macOS/Linux).
- Protects the user's active game: a test refuses to start if RimWorld is already running.
- Consolidates Bridge diagnostics and incremental `Player.log` diagnostics; `Player.log` remains
  useful even if the Bridge cannot connect.
- Keeps all game files, decompiled sources, indexes, diagnostics, and test save data local.

## Requirements

- CPython 3.14 or later
- [uv](https://docs.astral.sh/uv/) (recommended) or `pipx`
- .NET SDK capable of building `net472` projects
- A locally installed, legally obtained copy of RimWorld

The C# Bridge references RimWorld assemblies from the detected installation. No RimWorld binaries
are included in this repository.

## Installation

### Recommended: uv

```sh
git clone <your-repository-url> rimworld-mcp
cd rimworld-mcp
uv sync --extra dev --extra csharp-parser
uv run rimworld-mcp setup_toolchain --confirm
```

`setup_toolchain` restores ILSpyCmd through the project's local .NET tool manifest. It does not
install a global .NET tool.

Start the stdio server with:

```sh
uv run rimworld-mcp stdio
```

### pipx alternative

```sh
pipx install .
rimworld-mcp setup_toolchain --confirm
```

For development and Tree-sitter indexing, prefer the `uv sync --extra dev --extra csharp-parser`
command above because it uses the committed `uv.lock`.

## MCP client configuration

Use the executable in the project's virtual environment and pass `stdio`.

Windows example:

```toml
[mcp_servers.rimworld]
command = "C:\\path\\to\\rimworld-mcp\\.venv\\Scripts\\rimworld-mcp.exe"
args = ["stdio"]
```

See the ready-to-adapt templates in [examples/](examples/):

- [Codex TOML](examples/codex-mcp.toml)
- [Claude Code JSON](examples/claude-code.mcp.json)
- [Windows wrapper](examples/run-rimworld-mcp.cmd)
- [macOS/Linux wrapper](examples/run-rimworld-mcp.sh)

## First use

1. Configure the server in Codex, Claude Code, or another MCP client.
2. Call `rimworld_status` to confirm that RimWorld was discovered.
3. Call `setup_toolchain` with `confirm: true` if ILSpyCmd has not been restored yet.
4. Call `rebuild_index` once. The first decompilation can take several minutes.
5. Call `configure_workspace` before creating, changing, building, importing assets into, or
   testing a mod.

Only canonical paths contained by a registered workspace may be modified.

## Tools

### Research and installed mods

| Tool | Purpose |
| --- | --- |
| `rimworld_status` | Show discovered game paths, index freshness, and test status. |
| `setup_toolchain` | Restore ILSpyCmd; requires `confirm: true`. |
| `rebuild_index` | Decompile Core/DLC and rebuild Def, FTS, symbol, and reference indexes. |
| `search_defs` | Search indexed XML Defs. |
| `read_symbol` | Read indexed C# symbol metadata and source excerpts. |
| `search_source` | Regex-search decompiled Core/DLC C# and Def XML. |
| `list_installed_mods` | List local and Workshop mods discovered from `About.xml`. |
| `inspect_installed_mod` | Inspect and, on demand, decompile an installed mod's assemblies. |
| `search_installed_mod_source` | Search an installed mod's on-demand decompiled C# sources. |

### Workspace, build, and assets

| Tool | Purpose |
| --- | --- |
| `configure_workspace` | Register a mod-development workspace. |
| `create_mod` | Create an XML-only or C# mod skeleton. |
| `build_mod` | Validate an XML mod or build a C# mod and deploy produced DLLs. |
| `create_checkpoint` | Save a local checkpoint of a registered mod. |
| `restore_checkpoint` | Restore a checkpoint; requires `confirm: true`. |
| `import_mod_asset` | Copy a PNG/JPG texture or OGG/WAV sound into a registered mod. |
| `validate_mod_assets` | Check asset extensions, file signatures, and oversized files. |

### Test sessions and diagnostics

| Tool | Purpose |
| --- | --- |
| `run_test_cycle` | Start RimWorld with an isolated save-data folder and the diagnostic Bridge. |
| `test_status` | Read the shared current test session state. |
| `list_test_diagnostics` | List deduplicated Bridge and `Player.log` diagnostics. |
| `get_test_diagnostic` | Retrieve one diagnostic by hash. |
| `stop_test` | Clean up service-owned test links and the monitoring daemon; `terminate_game: true` also stops the game this service launched; requires `confirm: true`. |

## Test and diagnostic model

`run_test_cycle` writes a minimal `activeMods` set to a unique test save-data directory and never
alters the user's normal `ModsConfig.xml`. The generated load order is:

1. `ludeon.rimworld` (Core), always first.
2. Required `modDependencies`, resolved from `Mods/`, Steam Workshop, **and** `Data/` so that Core
   and installed DLC can satisfy dependencies. A missing hard dependency aborts the run.
3. The target mod and any requested companion mods.
4. `rimworldmcp.bridge`, so the diagnostic Bridge is actually loaded by the game.

`loadAfter` is treated as the soft ordering hint that RimWorld defines it to be: declarations that
resolve are used for ordering, and declarations that do not resolve are reported in the session
status as `skipped_load_after` instead of aborting the run. Hard dependency cycles and incompatible
selected mods still abort.

If the Bridge cannot be prepared (no .NET SDK, undetectable `Managed` directory, a conflicting
link), the run continues with `Player.log` diagnostics only and the reason is recorded under
`bridge` in the session status — it is never silently dropped.

The Python daemon accepts token-authenticated NDJSON only from `127.0.0.1`. The Bridge emits
errors, warnings, loaded-mod information, and startup performance data. The daemon also tails
the selected `Player.log` while the session is running, providing a fallback if the Bridge is not
available.

The server uses local interprocess locks to prevent concurrent index rebuilds and test launches. A
lock whose owning process no longer exists is reclaimed automatically, so a killed server cannot
deadlock future runs.

## Path discovery and overrides

The server recognizes the standard RimWorld layouts:

- Windows: `RimWorldWin64.exe` and `RimWorldWin64_Data/Managed`
- Linux: `RimWorldLinux` and `RimWorldLinux_Data/Managed`
- macOS: the `.app` bundle's `Contents/Info.plist`, executable, and
  `Contents/Resources/Data/Managed`

Override automatic discovery when needed:

```sh
RIMWORLD_MCP_GAME_PATH=/path/to/RimWorld
RIMWORLD_MCP_PLAYER_LOG=/path/to/Player.log
```

## Security and privacy

- Game data and decompiled source never leave the local machine.
- No game, mod, index, diagnostic, or asset data is submitted to an external service.
- Writes are restricted to registered workspaces, except for service-owned local cache and test
  data managed with `platformdirs`.
- Destructive operations require explicit `confirm: true` where applicable.
- The Bridge only connects to loopback and requires a fresh token for every test run.
- The server never force-terminates a user-owned RimWorld process. `stop_test` can only terminate
  processes it started itself, whose PIDs it recorded: the monitoring daemon, and — only with
  `terminate_game: true` — the game instance it launched.
- Cleanup only ever removes links it created (names prefixed `RimWorldMcp-` that resolve to the
  expected target). It removes the link, never the mod directory the link points at.

## Development

```sh
uv sync --extra dev --extra csharp-parser
uv run ruff check .
uv run mypy
uv run pytest
uv lock --check
```

The CI workflow validates Windows, macOS, and Ubuntu on Python 3.14. Real game launch tests are
deliberately manual because they require an installed game and user authorization.

## License

MIT. See [LICENSE](LICENSE).
