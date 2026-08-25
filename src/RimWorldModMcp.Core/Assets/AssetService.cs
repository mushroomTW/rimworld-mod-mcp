using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Assets;

/// <summary>可匯入的資產類別。</summary>
public enum AssetKind
{
    Texture,
    Sound,
}

/// <summary>資產檢查的一筆結果。</summary>
public sealed record AssetIssue(string Level, string File, string Message);

/// <summary>資產匯入的結果。</summary>
public sealed record AssetImport(string Asset, string Reference);

/// <summary>把圖片與音效匯入 Mod，並驗證格式正確。</summary>
public sealed class AssetService(WorkspaceRegistry workspaces)
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] AudioExtensions = [".ogg", ".wav"];

    /// <summary>超過這個大小只警告不阻擋——大檔會拖慢遊戲載入，但不是錯誤。</summary>
    private const long LargeFileBytes = 50L * 1024 * 1024;

    public AssetImport Import(string modPath, string sourcePath, AssetKind kind)
    {
        var mod = workspaces.AllowedMod(modPath);
        var source = Path.GetFullPath(sourcePath);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"來源檔案不存在：{source}");
        }

        var extension = Path.GetExtension(source).ToLowerInvariant();
        var allowed = kind == AssetKind.Texture ? ImageExtensions : AudioExtensions;

        if (!allowed.Contains(extension))
        {
            throw new ArgumentException(
                $"{kind} 不接受副檔名 {extension}；可用的是 {string.Join("、", allowed)}。",
                nameof(sourcePath));
        }

        var folder = kind == AssetKind.Texture ? "Textures" : "Sounds";
        var targetRoot = Path.Combine(mod, folder);
        Directory.CreateDirectory(targetRoot);

        var target = Path.Combine(targetRoot, Path.GetFileName(source));

        if (File.Exists(target) && !SameFile(source, target))
        {
            throw new IOException($"目標已存在且內容不同：{target}");
        }

        File.Copy(source, target, overwrite: true);

        // RimWorld 以「不含副檔名的相對路徑」引用資產。
        var reference = Path.Combine(folder, Path.GetFileNameWithoutExtension(source)).Replace('\\', '/');

        return new AssetImport(Path.GetRelativePath(mod, target).Replace('\\', '/'), reference);
    }

    /// <summary>檢查 Mod 內所有資產的副檔名、檔案簽名與大小。</summary>
    public IReadOnlyList<AssetIssue> Validate(string modPath)
    {
        var mod = workspaces.AllowedMod(modPath);
        var issues = new List<AssetIssue>();

        foreach (var folder in (ReadOnlySpan<string>)["Textures", "Sounds"])
        {
            var root = Path.Combine(mod, folder);

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                Inspect(file, mod, issues);
            }
        }

        return issues;
    }

    private static void Inspect(string file, string mod, List<AssetIssue> issues)
    {
        var relative = Path.GetRelativePath(mod, file).Replace('\\', '/');
        var extension = Path.GetExtension(file).ToLowerInvariant();

        if (!ImageExtensions.Contains(extension) && !AudioExtensions.Contains(extension))
        {
            issues.Add(new AssetIssue("warning", relative, "不支援的資產副檔名。"));
            return;
        }

        Span<byte> header = stackalloc byte[12];
        int read;

        try
        {
            using var stream = File.OpenRead(file);
            read = stream.ReadAtLeast(header, 12, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            issues.Add(new AssetIssue("error", relative, "無法讀取檔案。"));
            return;
        }

        if (!SignatureMatches(extension, header[..read]))
        {
            issues.Add(new AssetIssue("error", relative, "檔案內容與副檔名不符或已損毀。"));
        }

        if (new FileInfo(file).Length > LargeFileBytes)
        {
            issues.Add(new AssetIssue("warning", relative, "資產超過 50 MiB，可能拖慢載入。"));
        }
    }

    /// <summary>比對檔案開頭的 magic bytes。副檔名對了但內容不是那個格式，遊戲會載入失敗。</summary>
    private static bool SignatureMatches(string extension, ReadOnlySpan<byte> header) => extension switch
    {
        ".png" => header.StartsWith(PngSignature),
        ".jpg" or ".jpeg" => header.StartsWith(JpegSignature),
        ".wav" => header.StartsWith("RIFF"u8) && header.Length >= 12 && header[8..12].SequenceEqual("WAVE"u8),
        ".ogg" => header.StartsWith("OggS"u8),
        _ => false,
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8];

    private static bool SameFile(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);

        return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }
}
