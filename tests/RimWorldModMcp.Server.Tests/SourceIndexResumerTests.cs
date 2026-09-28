using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;
using RimWorldModMcp.Indexing;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Server.Tests;

public sealed class SourceIndexResumerTests : IDisposable
{
    private readonly string _root;
    private readonly StoreDirectories _store;

    public SourceIndexResumerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-resumer-" + Guid.NewGuid().ToString("n")[..12]);
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAndStopHostedService()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddRimWorldCore();
        builder.Services.AddRimWorldIndexing();
        builder.Services.AddRimWorldDiagnostics();
        builder.Services.AddSingleton(_store);
        builder.Services.AddHostedService<SourceIndexResumer>();

        using var host = builder.Build();
        await host.StartAsync();
        await Task.Delay(100);
        await host.StopAsync();

        var indexer = host.Services.GetRequiredService<SourceIndexer>();
        Assert.False(indexer.Progress.Running);
    }
}
