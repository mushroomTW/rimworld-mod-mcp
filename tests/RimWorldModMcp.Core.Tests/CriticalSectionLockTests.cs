using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Tests;

public sealed class CriticalSectionLockTests : IDisposable
{
    private readonly string _root;
    private readonly StoreDirectories _store;

    public CriticalSectionLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-lock-" + Guid.NewGuid().ToString("n")[..12]);
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SecondAcquireIsRejectedWhileHolderIsAlive()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        locks.Acquire("index");

        var error = Assert.Throws<LockHeldException>(() => locks.Acquire("index"));
        Assert.Contains("already in progress", error.Message, StringComparison.Ordinal);
        Assert.Equal("index", error.LockName);

        // ToolGuard.IsExpected 只認得 InvalidOperationException，繼承關係是契約的一部分。
        Assert.IsType<InvalidOperationException>(error, exactMatch: false);
    }

    /// <summary>
    /// 有界等待版本：鎖被持有時等不到就拋出，放掉之後就取得得到。
    /// 這是「Mod 索引等 rebuild_index 寫完」那條路徑的基礎。
    /// </summary>
    [Fact]
    public async Task AcquireWithTimeoutWaitsForTheHolderToRelease()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });
        var token = locks.Acquire("index");

        // 逾時為零且鎖仍被持有：立刻失敗，不是等到逾時才失敗。
        var started = DateTime.UtcNow;
        Assert.Throws<LockHeldException>(() => locks.Acquire("index", TimeSpan.Zero));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "逾時為零時不該等待");

        // 持有者放掉之後，等待中的呼叫就取得得到。
        var released = Task.Run(async () =>
        {
            await Task.Delay(300);
            locks.Release("index", token);
        });

        var waited = locks.Acquire("index", TimeSpan.FromSeconds(10));

        Assert.NotNull(waited);
        await released;
    }

    /// <summary>
    /// 建檔撞上既有鎖檔、但在判斷前持有者就釋放了：那是「剛好放掉」，必須重試，
    /// 不能讓 IOException 漏出去——有界等待只重試 LockHeldException，漏出去就整個失敗。
    /// </summary>
    [Fact]
    public async Task ContendedAcquireNeverLeaksIOExceptionWhenTheHolderReleases()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var token = locks.Acquire("index", TimeSpan.FromSeconds(30));
                locks.Release("index", token);
            }
        }));

        await Task.WhenAll(workers);
    }

    /// <summary>等不到就必須拋出，不能無限期卡住——呼叫端要能把它當成可重試的失敗。</summary>
    [Fact]
    public void AcquireWithTimeoutThrowsWhenTheHolderKeepsIt()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });
        locks.Acquire("index");

        var started = DateTime.UtcNow;

        Assert.Throws<LockHeldException>(() => locks.Acquire("index", TimeSpan.FromMilliseconds(250)));

        var elapsed = DateTime.UtcNow - started;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(200), $"應該等過一段時間，實際 {elapsed.TotalMilliseconds:0}ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"不該無限期等待，實際 {elapsed.TotalMilliseconds:0}ms");
    }

    [Fact]
    public void ReleaseAllowsTheNextAcquire()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        var token = locks.Acquire("index");
        locks.Release("index", token);

        // 不拋出且回傳有效 token 即代表成功取得。
        var nextToken = locks.Acquire("index");
        Assert.NotNull(nextToken);
    }

    [Fact]
    public void ReleaseWithWrongTokenDoesNothing()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        locks.Acquire("index");
        locks.Release("index", "not-the-real-token");

        Assert.Throws<LockHeldException>(() => locks.Acquire("index"));
    }

    [Fact]
    public void StaleLockFromDeadHolderIsReclaimed()
    {
        var alive = new FakeProcessHost { Alive = true };
        new CriticalSectionLock(_store, alive).Acquire("index");

        // 持有者程序消失後，下一個取用者應該要能接管殘骸鎖。
        var dead = new FakeProcessHost { Alive = false };
        var token = new CriticalSectionLock(_store, dead).Acquire("index");
        Assert.NotNull(token);
    }

    /// <summary>
    /// Unix 上 FileShare.None 是「先建檔、再上 flock」兩步：中間有一瞬間鎖檔是空的且沒鎖住。
    /// 剛建立的空檔是正在寫入的活鎖，不能當殘骸接管，否則兩個程序會同時持有臨界區。
    /// </summary>
    [Fact]
    public void FreshlyCreatedEmptyLockFileIsTreatedAsHeld()
    {
        Directory.CreateDirectory(_store.LocksDirectory);
        File.WriteAllText(Path.Combine(_store.LocksDirectory, "index.json"), string.Empty);

        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = false });

        Assert.Throws<LockHeldException>(() => locks.Acquire("index"));
    }

    /// <summary>寫到一半就崩潰留下的空鎖檔，過了寬限期仍要能接管，否則工具永遠卡住。</summary>
    [Fact]
    public void StaleEmptyLockFileIsReclaimed()
    {
        Directory.CreateDirectory(_store.LocksDirectory);
        var path = Path.Combine(_store.LocksDirectory, "index.json");
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = false });

        Assert.NotNull(locks.Acquire("index"));
    }

    [Fact]
    public void LockIsReclaimedWhenPidWasRecycled()
    {
        var original = new FakeProcessHost { Alive = true, StartTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        new CriticalSectionLock(_store, original).Acquire("index");

        // PID 仍然活著，但啟動時間對不上——代表這個 PID 已經被重用給另一個程序，
        // 原持有者其實早就結束了。這是 Python 版沒有的防護。
        var recycled = new FakeProcessHost { Alive = true, StartTime = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc) };
        var token = new CriticalSectionLock(_store, recycled).Acquire("index");
        Assert.NotNull(token);
    }

    [Fact]
    public void HoldReleasesOnDispose()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        using (locks.Hold("index"))
        {
            Assert.Throws<LockHeldException>(() => locks.Acquire("index"));
        }

        locks.Acquire("index");
    }

    private sealed class FakeProcessHost : IProcessHost
    {
        public bool Alive { get; init; }

        public DateTime? StartTime { get; init; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public bool IsAlive(int processId) => Alive;

        public bool Terminate(int processId, DateTime? expectedStartUtc = null) => true;

        public DateTime? StartTimeUtc(int processId) => StartTime;
    }
}
