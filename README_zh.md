# rimworld-mcp

`rimworld-mcp` 是一個完全在本機執行、跨平台的 [Model Context Protocol](https://modelcontextprotocol.io/)
RimWorld 模組開發伺服器。它讓 Codex、Claude Code 與其他 MCP client 能研究 RimWorld API／Def、建立與建置 Mod、驗證資產，以及執行隔離的遊戲測試。

本專案的設計與架構參考了 [Modmixer](https://github.com/lebek/modmixer)，但屬獨立實作：不含 Modmixer 的原始碼、不相依於 Modmixer，也不會與 Modmixer 或任何其他外部服務通訊。

> 本專案**不包含 Steam Workshop 發佈、上傳或任何外部內容傳輸功能**。

## 特色

- 支援 Windows、macOS、Linux，要求 CPython 3.14+。
- 使用官方 Python MCP SDK 2.x 與 stdio `MCPServer`。
- 偵測本機 RimWorld、Steam Workshop、設定檔與 `Player.log`；遊戲與日誌位置可用環境變數覆寫。
- 透過專案私有的 ILSpyCmd 反編譯 Core 與已安裝 DLC，建立 SQLite FTS5 Def 索引、C# 符號與原始碼索引。
- 已安裝 optional binding 時優先使用 Tree-sitter C#；沒有 binding 時安全退回 regex parser。
- 以按需、僅本機方式反編譯及搜尋第三方已安裝 Mod 的組件。
- 建立 XML-only 或 C# Mod 骨架，驗證 metadata／資產，並將成功編譯的 C# DLL 部署到 Mod 的 `Assemblies/`。
- 使用隔離 `-savedatafolder`、每次測試的 Bridge token、Windows junction 或 macOS/Linux symbolic link 執行測試。
- 保護使用者現有遊戲：RimWorld 已開啟時拒絕開始測試。
- 合併 Bridge 與增量 `Player.log` 診斷；Bridge 連不上時仍可由 `Player.log` 提供問題線索。
- 遊戲檔、反編譯結果、索引、診斷與測試 savedata 都只保留在本機。

## 系統需求

- CPython 3.14 或更新版本
- [uv](https://docs.astral.sh/uv/)（建議）或 `pipx`
- 能建置 `net472` 專案的 .NET SDK
- 本機已合法安裝的 RimWorld

C# Bridge 會引用偵測到的 RimWorld 組件；本 repository 不包含任何 RimWorld 二進位檔。

## 安裝

### 建議方式：uv

```sh
git clone <你的-repository-url> rimworld-mcp
cd rimworld-mcp
uv sync --extra dev --extra csharp-parser
uv run rimworld-mcp setup_toolchain --confirm
```

`setup_toolchain` 會透過專案本機 .NET tool manifest 還原 ILSpyCmd，不會安裝任何全域 .NET tool。

以 stdio 啟動 MCP server：

```sh
uv run rimworld-mcp stdio
```

### pipx 替代方式

```sh
pipx install .
rimworld-mcp setup_toolchain --confirm
```

進行開發或啟用 Tree-sitter 索引時，仍建議使用
`uv sync --extra dev --extra csharp-parser`，以遵循已提交的 `uv.lock`。

## MCP client 設定

使用專案虛擬環境內的 executable，並傳入 `stdio`。

Windows 範例：

```toml
[mcp_servers.rimworld]
command = "C:\\path\\to\\rimworld-mcp\\.venv\\Scripts\\rimworld-mcp.exe"
args = ["stdio"]
```

可調整的範本位於 [examples/](examples/)：

- [Codex TOML](examples/codex-mcp.toml)
- [Claude Code JSON](examples/claude-code.mcp.json)
- [Windows wrapper](examples/run-rimworld-mcp.cmd)
- [macOS/Linux wrapper](examples/run-rimworld-mcp.sh)

## 首次使用

1. 在 Codex、Claude Code 或其他 MCP client 設定 server。
2. 呼叫 `rimworld_status`，確認 RimWorld 已被偵測。
3. 若尚未還原 ILSpyCmd，呼叫 `setup_toolchain` 並傳入 `confirm: true`。
4. 呼叫一次 `rebuild_index`。首次反編譯可能耗時數分鐘。
5. 在建立、修改、建置、匯入資產或測試 Mod 前，先呼叫 `configure_workspace`。

只有位於已登記 workspace 內的 canonical path 可以被修改。

## 工具

### 研究與已安裝 Mod

| 工具 | 用途 |
| --- | --- |
| `rimworld_status` | 顯示偵測到的遊戲路徑、索引新鮮度與測試狀態。 |
| `setup_toolchain` | 還原 ILSpyCmd；需要 `confirm: true`。 |
| `rebuild_index` | 反編譯 Core/DLC，重建 Def、FTS、符號與引用索引。 |
| `search_defs` | 搜尋已索引 XML Def。預設只回 metadata；`include_xml: true` 會附上截斷過的 XML。 |
| `read_def` | 依 `defName`（或抽象 Def 的 `Name`）讀取單一 Def 的完整 XML。 |
| `read_symbol` | 讀取已索引 C# 符號 metadata 與原始碼節錄。 |
| `search_source` | 對反編譯 Core/DLC C# 與 Def XML 進行 regex 搜尋。 |
| `list_installed_mods` | 依 `About.xml` 列出 local 與 Workshop Mod。 |
| `inspect_installed_mod` | 檢查並按需反編譯已安裝 Mod 的組件。 |
| `search_installed_mod_source` | 搜尋已安裝 Mod 按需反編譯後的 C#。 |

### Workspace、建置與資產

| 工具 | 用途 |
| --- | --- |
| `configure_workspace` | 登記 Mod 開發 workspace。 |
| `create_mod` | 建立 XML-only 或 C# Mod 骨架。 |
| `build_mod` | 驗證 XML Mod，或建置 C# Mod 並部署輸出 DLL。輸出目錄依 csproj 的 `TargetFramework` 判斷，不再假設是 `net472`。 |
| `create_checkpoint` | 建立已登記 Mod 的本機 checkpoint。 |
| `restore_checkpoint` | 還原 checkpoint；需要 `confirm: true`。 |
| `import_mod_asset` | 將 PNG/JPG 圖片或 OGG/WAV 音效複製到已登記 Mod。`kind` 只接受 `texture` 或 `sound`。 |
| `validate_mod_assets` | 檢查資產副檔名、檔案簽名與過大檔案。 |

### 測試 session 與診斷

| 工具 | 用途 |
| --- | --- |
| `run_test_cycle` | 以隔離 save-data folder 與診斷 Bridge 啟動 RimWorld。 |
| `test_status` | 讀取共用的目前測試 session 狀態。 |
| `list_test_diagnostics` | 列出已去重的 Bridge 與 `Player.log` 診斷。 |
| `get_test_diagnostic` | 依 `diagnostic_hash` 讀取單一診斷。 |
| `stop_test` | 清理服務建立的測試連結與監控 daemon；`terminate_game: true` 才會終止本服務啟動的遊戲；需要 `confirm: true`。 |

## 測試與診斷模型

`run_test_cycle` 只把最小啟用集寫進唯一的測試 savedata directory，不會修改使用者正常使用的 `ModsConfig.xml`。產生的載入順序為：

1. `ludeon.rimworld`（Core），一律排第一。
2. 必要的 `modDependencies`，來源包含 `Mods/`、Steam Workshop **以及** `Data/`，因此 Core 與已安裝的 DLC 也能滿足相依。硬相依缺少會中止本次測試。
3. 目標 Mod 與指定的 companion Mod。
4. `rimworldmcp.bridge`，讓診斷 Bridge 真的被遊戲載入。

`loadAfter` 依 RimWorld 的定義視為軟性排序提示：找得到的用於排序，找不到的記入 session status 的 `skipped_load_after`，不會中止測試。硬相依循環與已選 Mod 的不相容關係仍會中止。

Bridge 無法準備好時（沒有 .NET SDK、偵測不到 `Managed` 目錄、連結名稱被佔用），測試仍會以 `Player.log` 診斷繼續，原因記在 session status 的 `bridge` 欄位——不會靜默忽略。

Python daemon 只在 `127.0.0.1` 接受以 token 驗證的 NDJSON。Bridge 會傳送錯誤、警告、已載入 Mod 與啟動效能資料。測試 session 運行期間 daemon 也會 tail 指定的 `Player.log`，因此 Bridge 不可用時仍有診斷 fallback。

server 使用本機跨程序鎖，避免同時重建索引或啟動多個遊戲測試。持有者程序已不存在的鎖會自動接管，因此 server 被強制終止不會讓後續工作永久卡死。

## 路徑偵測與覆寫

server 可辨識標準 RimWorld 目錄：

- Windows：`RimWorldWin64.exe` 與 `RimWorldWin64_Data/Managed`
- Linux：`RimWorldLinux` 與 `RimWorldLinux_Data/Managed`
- macOS：`.app` bundle 的 `Contents/Info.plist`、執行檔，以及 `Contents/Resources/Data/Managed`

需要時可覆寫自動偵測：

```sh
RIMWORLD_MCP_GAME_PATH=/path/to/RimWorld
RIMWORLD_MCP_PLAYER_LOG=/path/to/Player.log
```

## 安全與隱私

- 遊戲資料與反編譯原始碼不會離開本機。
- 不會把遊戲、Mod、索引、診斷或資產資料送至外部服務。
- 除了由 `platformdirs` 管理的服務快取與測試資料外，寫入都限制於已登記 workspace。
- 適用時，破壞性操作需要明確的 `confirm: true`。
- Bridge 只會連線至 loopback，且每次測試都使用新的 token。
- server 絕不強制終止使用者自己的 RimWorld 程序。`stop_test` 只能終止自己啟動並記錄過 pid 的程序：監控 daemon，以及在 `terminate_game: true` 時本服務啟動的那個遊戲實例。
- 清理只會移除本服務建立的連結（名稱以 `RimWorldMcp-` 開頭且指向預期目標）；移除的是連結本身，不會刪除它指向的 Mod 目錄。

## 開發

```sh
uv sync --extra dev --extra csharp-parser
uv run ruff check .
uv run mypy
uv run pytest
uv lock --check
```

CI 會在 Python 3.14 的 Windows、macOS、Ubuntu 驗證。真實遊戲啟動測試必須由使用者明確授權，因為它需要已安裝的遊戲。

## 授權

MIT，請見 [LICENSE](LICENSE)。
