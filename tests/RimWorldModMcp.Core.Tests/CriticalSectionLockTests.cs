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

        var error = Assert.Throws<InvalidOperationException>(() => locks.Acquire("index"));
        Assert.Contains("已有進行中", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAllowsTheNextAcquire()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        var token = locks.Acquire("index");
        locks.Release("index", token);

        // 不拋出即代表成功取得。
        locks.Acquire("index");
    }

    [Fact]
    public void ReleaseWithWrongTokenDoesNothing()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        locks.Acquire("index");
        locks.Release("index", "not-the-real-token");

        Assert.Throws<InvalidOperationException>(() => locks.Acquire("index"));
    }

    [Fact]
    public void StaleLockFromDeadHolderIsReclaimed()
    {
        var alive = new FakeProcessHost { Alive = true };
        new CriticalSectionLock(_store, alive).Acquire("index");

        // 持有者程序消失後，下一個取用者應該要能接管殘骸鎖。
        var dead = new FakeProcessHost { Alive = false };
        new CriticalSectionLock(_store, dead).Acquire("index");
    }

    [Fact]
    public void LockIsReclaimedWhenPidWasRecycled()
    {
        var original = new FakeProcessHost { Alive = true, StartTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        new CriticalSectionLock(_store, original).Acquire("index");

        // PID 仍然活著，但啟動時間對不上——代表這個 PID 已經被重用給另一個程序，
        // 原持有者其實早就結束了。這是 Python 版沒有的防護。
        var recycled = new FakeProcessHost { Alive = true, StartTime = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc) };
        new CriticalSectionLock(_store, recycled).Acquire("index");
    }

    [Fact]
    public void HoldReleasesOnDispose()
    {
        var locks = new CriticalSectionLock(_store, new FakeProcessHost { Alive = true });

        using (locks.Hold("index"))
        {
            Assert.Throws<InvalidOperationException>(() => locks.Acquire("index"));
        }

        locks.Acquire("index");
    }

    private sealed class FakeProcessHost : IProcessHost
    {
        public bool Alive { get; init; }

        public DateTime? StartTime { get; init; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public bool IsAlive(int processId) => Alive;

        public bool Terminate(int processId) => true;

        public DateTime? StartTimeUtc(int processId) => StartTime;
    }
}
