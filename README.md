# rimworld-mod-mcp

A local-only [Model Context Protocol](https://modelcontextprotocol.io/) server that lets Claude Code, Codex, and other MCP clients research RimWorld's API and Defs, create and build mods, validate assets, and test them in an isolated game session.

This project is inspired by [Modmixer](https://github.com/lebek/modmixer) but is an independent implementation: it contains no Modmixer source code, does not depend on Modmixer, and never communicates with Modmixer or any other external service.

> This project contains **no Steam Workshop publishing, uploading, or any external content transfer**.

[繁體中文](README_zh.md)

## Highlights

- Runs on Windows, macOS, and Linux; ships as a single dotnet tool.
- **Reads IL metadata directly to build the symbol index** — roughly a hundred thousand symbols in seconds, with complete inheritance chains and real type signatures.
- SQLite FTS5 full-text search over Defs, symbols, and decompiled source.
- In-process decompilation via `ICSharpCode.Decompiler`. No external tools, no toolchain setup step.
- Build failures return **structured diagnostics** (error code, file, line, column) instead of a wall of MSBuild output.
- Tests mods with an isolated save folder and temporary directory links, leaving your real saves and settings untouched.
- Merges diagnostics from the in-game bridge and `Player.log`. If the bridge cannot connect, `Player.log` still works and the reason is reported rather than silently degraded.
- Game files, decompiled output, indexes, and diagnostics all stay on your machine.

## Requirements

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- A legitimately installed copy of RimWorld

> [!NOTE]
> When running as a self-contained single executable, the server itself does not require a local .NET 10 Runtime. However, a local .NET SDK is still required at runtime for `build_mod` (compiling C# mods) and `run_test_cycle` (compiling the in-game test bridge on the fly).

The C# bridge references the RimWorld assemblies it detects; this repository contains no RimWorld binaries.

## Installation

As a global dotnet tool:

```bash
dotnet tool install -g RimWorldModMcp
```

From source:

```bash
dotnet pack src/RimWorldModMcp.Server -c Release -o ./nupkg
dotnet tool install -g RimWorldModMcp --add-source ./nupkg
```

### Publishing as a standalone single executable (no .NET 10 Runtime required)

To run without installing .NET 10 or to distribute as a standalone executable, publish as a self-contained single file:

```bash
# Windows (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# macOS (Apple Silicon)
dotnet publish src/RimWorldModMcp.Server -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# Linux (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

> **Note**: The publish output contains the single executable and the `bridge/` directory. Keep the `bridge/` directory alongside the executable if you need `run_test_cycle` (isolated in-game testing); all other core features (Def search, decompilation, symbol indexing) are fully embedded inside the executable.

## MCP client configuration

When installed as a dotnet tool, `rimworld-mod-mcp` is on your PATH, so no absolute paths are needed:

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

`create_mod`, `build_mod`, and `run_test_cycle` accept any local directory path; there is no write boundary — scoping writes is left to your MCP client's permission prompts and to you.

Templates live in [examples/](examples/): [Claude Code](examples/claude-code.mcp.json), [Codex](examples/codex-mcp.toml), [custom port and game path](examples/custom-port.mcp.json).

If you use the standalone executable (or want to point to local build output), point the command directly to the executable path — see [local-build.mcp.json](examples/local-build.mcp.json):

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "C:\\path\\to\\publish\\RimWorldModMcp.Server.exe",
      "args": ["stdio"]
    }
  }
}
```

## First use

1. Configure the server in your MCP client.
2. Call `rimworld_status` to confirm RimWorld was detected.
3. Call `rebuild_index` once. Def and symbol indexing finishes in seconds and is immediately usable; the source-code full-text index continues in the background, and `search_source` reports `source_indexed: false` until it completes.

## Tools

### Researching the game

| Tool | Purpose |
| --- | --- |
| `rimworld_status` | Detected paths and index state. `index.source_index` says whether the source full-text layer is running, failed (`error`), or done. |
| `rebuild_index` | Rebuild the Def and symbol indexes. |
| `search_defs` | Search Defs by name, label, or description. |
| `read_def` | Read one Def's full XML. Look up abstract Defs by their `Name` attribute. |
| `read_symbol` | Signature, full inheritance chain, and interfaces for a symbol; optionally decompiled source. `assembly` narrows to one assembly (or one version of a multi-version mod). Zero hits return `suggestions`. |
| `list_symbols` | Browse without a keyword: a namespace lists its top-level types and child namespaces; a type lists its members. An unknown parent is an error, not an empty list. |
| `find_descendants` | Find every class deriving from a given type; a short name works when unambiguous. Zero hits return `suggestions`. |
| `search_source` | Regex search (.NET syntax, always case-insensitive) over decompiled source: the game by default, or one installed mod with `package_id` (first use decompiles in the background; `indexing=true` means retry shortly). `assembly` (e.g. `1.6/`) restricts a mod search to one version's DLL. Prefer simple literals (`CurTimeSpeed`); `a|b` matches either branch. |
| `read_source_file` | Read a whole decompiled file (game or mod) by the `assembly` and `file` a search returned, paging by line (`start_line` + `max_bytes`). Check `source_indexed` before trusting empty results. |
| `find_def_usages` | Find where a Def is referenced, across XML cross-references and C# `DefOf` fields. Use it to verify translatable fields before hand-writing `Languages/` XML. |

### Installed mods

| Tool | Purpose |
| --- | --- |
| `list_installed_mods` | List local and Workshop mods. Filter with `package_id` and page with `limit`/`offset`; `include_details` adds dependencies and versions. |
| `inspect_installed_mod` | Decompile and index a mod's assemblies on demand. Locks are per-`packageId`, so a large mod (minutes on first run) does not block other mods. Each assembly is keyed `mod:<packageId>:<relative DLL path>`, e.g. `mod:cj.rimtalk:1.6/Assemblies/RimTalk.dll`. |

### Build

| Tool | Purpose |
| --- | --- |
| `create_mod` | Create a mod skeleton, optionally with a C# project whose csproj already references the game assemblies. |
| `build_mod` | Validate an XML mod, or build a C# mod and deploy its DLL; compiler errors come back as structured diagnostics. |

The tool set deliberately covers only what an AI coding agent cannot do on its own: querying Defs and decompiled source from the game and other mods, building, and launching the game in an isolated test session. Copying files, creating directories, and snapshots are things the agent already does natively, so no tools are provided for them — use Git for version control.

### Testing and diagnostics

| Tool | Purpose |
| --- | --- |
| `run_test_cycle` | Launch RimWorld in an isolated session to test a mod. Use it for in-game acceptance (time-speed feel, rendering) that static analysis cannot cover. |
| `test_status` | Current session state, including whether the bridge and daemon are healthy. |
| `stop_test` | Stop the session and clean up; requires `confirm: true`. |
| `list_test_diagnostics` | List collected errors and warnings. Poll with `since_at` (the previous `latest_at`) and `wait_seconds` to long-poll for new entries instead of re-reading the list. |
| `get_test_diagnostic` | Fetch one diagnostic in full by hash. |

## The three indexing tiers

Indexing is deliberately layered so the most useful data is ready first:

| Tier | Contents | Cost |
| --- | --- | --- |
| 1 | Def XML and IL metadata symbols | Seconds; the bulk of `rebuild_index` |
| 2 | Single-member decompilation | Milliseconds, computed on demand by `read_symbol` |
| 3 | Full-assembly decompiled source, full-text indexed | Minutes, runs in the background |

Once tier 1 completes, Def search, symbol lookup, inheritance chains, and `read_symbol` all work. Only `search_source` waits on tier 3.

## How test sessions stay isolated

- `-savedatafolder` points at a temporary directory, so your real saves and settings are untouched.
- A generated `ModsConfig.xml` activates only the mod under test and its dependencies, carrying over your existing game version and known DLC.
- Temporary links in the Mods folder point at your workspace, so edits take effect without copying files. Windows uses junctions (no administrator rights needed); macOS and Linux use symbolic links.
- Starting a test is refused while the game is already running — it would interfere with your own save.
- Stopping a session removes the links, terminates the daemon, and cleans up the temporary save data. **Link removal never recurses into the target, so your mod source is never touched.**

## Security and privacy

- The only tools that write files are `create_mod`, `build_mod`, and `run_test_cycle`, and they write only to the mod directory you pass (test sessions additionally use an isolated temporary save directory). There is no path allow-list; rely on your MCP client's tool-permission prompts.
- No external network communication. Game files, decompiled output, indexes, and diagnostics stay local.
- The bridge activates only when `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` is present and connects only to `127.0.0.1`. Each test session uses a single-use token; the token is never written to diagnostic records and never appears in tool responses.
- Destructive operations (`stop_test`) require an explicit `confirm: true`.

## Environment variables

| Variable | Purpose |
| --- | --- |
| `RIMWORLD_MOD_MCP_GAME_PATH` | Override RimWorld installation detection. |
| `RIMWORLD_MOD_MCP_PLAYER_LOG` | Override the `Player.log` location. |
| `RIMWORLD_MOD_MCP_BRIDGE_PORT` | Loopback port for the diagnostics daemon; defaults to 49460. |
| `RIMWORLD_MANAGED_DIR` | Passed to MSBuild to resolve game assemblies; injected automatically. |

## Development

```bash
dotnet build RimWorldModMcp.slnx
dotnet test RimWorldModMcp.slnx
```

CI runs the platform contract tests on Windows, macOS, and Ubuntu — junction behaviour and process liveness semantics differ per platform, so verifying on only one would lose the protection they provide.

Development subcommands (undocumented in the tool listing, useful for troubleshooting):

```bash
dotnet run --project src/RimWorldModMcp.Server -- detect      # show detected paths
dotnet run --project src/RimWorldModMcp.Server -- index       # build the index and report stats
dotnet run --project src/RimWorldModMcp.Server -- symbols     # run symbol reading only
dotnet run --project src/RimWorldModMcp.Server -- decompile X # decompile symbol X
dotnet run --project src/RimWorldModMcp.Server -- linkcheck P  # diagnose why a test link was not cleaned up
```

## License

MIT, see [LICENSE](LICENSE).
