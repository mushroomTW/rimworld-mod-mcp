# AGENTS.md

## 發版流程

發版由 CI 自動完成，**不要手動建立或推送 `v*` 標籤**。

1. 修改 `src/RimWorldModMcp.Server/RimWorldModMcp.Server.csproj` 的 `<Version>`。這是版本號的唯一來源：
   CI 的 `pack` 安裝步驟與 `tag` job 都從這裡讀取，不必同步改 `ci.yml`。
2. commit 並 push 到 `main`。
3. `.github/workflows/ci.yml` 在 `platform-contracts`、`test`、`pack` 全部通過後執行 `tag` job：
   若遠端沒有 `v<版本>` 標籤，就建立並推送標籤，再用 `gh workflow run release.yml --ref <標籤>` 派送 Release；
   標籤已存在則略過，所以版本號沒變的 push 不會重複發版。
4. `.github/workflows/release.yml` 在三個平台各自發佈單一執行檔，並建立 GitHub Release **草稿**。
   發佈前到 GitHub 補 release notes、檢查附件，再手動按發佈。

注意：

- `tag` job 靠 `workflow_dispatch` 派送 Release，因為 `GITHUB_TOKEN` 推的標籤不會觸發其他工作流程。
  手動推 `v*` 標籤仍會觸發 `release.yml`，但通常不需要。
- 只有 push 到 `main` 才會發版；pull request 與其他分支只跑測試與打包。
- push 前需取得使用者同意（見使用者的全域指示）。
