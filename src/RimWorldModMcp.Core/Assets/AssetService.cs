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
public sealed class AssetService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] AudioExtensions = [".ogg", ".wav"];

    /// <summary>超過這個大小只警告不阻擋——大檔會拖慢遊戲載入，但不是錯誤。</summary>
    private const long LargeFileBytes = 50L * 1024 * 1024;

    public AssetImport Import(ValidatedModPath modPath, string sourcePath, AssetKind kind)
    {
        var mod = modPath.Value;
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

        if (File.Exists(target))
        {
            // 目標若是 symlink，File.Copy 會跟著它把內容寫到連結指向的地方——
            // 可能在工作區之外。一律拒絕，不猜測使用者的意圖。
            if (new FileInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"目標是一個連結，拒絕覆寫：{target}");
            }

            if (!SameFile(source, target))
            {
                throw new IOException($"目標已存在且內容不同：{target}");
            }
        }

        File.Copy(source, target, overwrite: true);

        // RimWorld 以「不含副檔名的相對路徑」引用資產。
        var reference = Path.Combine(folder, Path.GetFileNameWithoutExtension(source)).Replace('\\', '/');

        return new AssetImport(Path.GetRelativePath(mod, target).Replace('\\', '/'), reference);
    }

    /// <summary>檢查 Mod 內所有資產的副檔名、檔案簽名與大小。</summary>
    public IReadOnlyList<AssetIssue> Validate(ValidatedModPath modPath)
    {
        var mod = modPath.Value;
        var issues = new List<AssetIssue>();

        foreach (var folder in (ReadOnlySpan<string>)["Textures", "Sounds"])
        {
            var root = Path.Combine(mod, folder);

            if (!Directory.Exists(root))
            {
                continue;
            }

            // 不跟隨 symlink / junction：跟進去的話，指向別處的連結會把
            // 工作區外的檔案掃進來，自我指涉的連結則造成無界遞迴。
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
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
        long length;

        try
        {
            using var stream = File.OpenRead(file);
            read = stream.ReadAtLeast(header, 12, throwOnEndOfStream: false);
            length = stream.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 檔案在列舉與讀取之間消失或被鎖住時，回報這一筆就好，
            // 不能讓一個檔案毀掉整次驗證。
            issues.Add(new AssetIssue("error", relative, "無法讀取檔案。"));
            return;
        }

        if (!SignatureMatches(extension, header[..read]))
        {
            issues.Add(new AssetIssue("error", relative, "檔案內容與副檔名不符或已損毀。"));
        }

        if (length > LargeFileBytes)
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

        if (a.Length != b.Length)
        {
            return false;
        }

        // 這個 guard 存在的目的就是防止靜默覆寫不同的內容，
        // 所以必須比對內容雜湊——長度加時間戳會把「同秒寫入的同大小不同檔」誤判為相同。
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);

        var leftHash = sha.ComputeHash(leftStream);
        sha.Initialize();
        var rightHash = sha.ComputeHash(rightStream);

        return leftHash.AsSpan().SequenceEqual(rightHash);
    }
}
