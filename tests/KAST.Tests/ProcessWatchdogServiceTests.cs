using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using KAST.Tests.Helpers;
using KAST.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace KAST.Tests;

public class ProcessWatchdogServiceTests : IDisposable
{
    private readonly KastDbContext _db;
    private readonly IProcessManagerService _processManager;
    private readonly IAppEventBroadcaster _broadcaster;
    private readonly IServerConsoleLogTailer _tailer;
    private readonly IServerInstanceService _serverService;
    private readonly ServiceProvider _provider;
    private readonly ProcessWatchdogService _sut;
    private bool _restartSucceeds;

    public ProcessWatchdogServiceTests()
    {
        _db = DbHelper.CreateInMemoryDb();
        _processManager = Substitute.For<IProcessManagerService>();
        _broadcaster = Substitute.For<IAppEventBroadcaster>();
        _tailer = Substitute.For<IServerConsoleLogTailer>();
        _serverService = Substitute.For<IServerInstanceService>();
        _restartSucceeds = true;

        _serverService.StartInstanceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                if (!_restartSucceeds)
                    throw new IOException("start failed");

                var id = callInfo.Arg<int>();
                var instance = _db.ServerInstances.First(i => i.Id == id);
                instance.Status = ServerInstanceStatus.Running;
                instance.ProcessId = 7777;
                await _db.SaveChangesAsync();
            });

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<IProcessManagerService>(_processManager);
        services.AddSingleton<IAppEventBroadcaster>(_broadcaster);
        services.AddSingleton<IServerConsoleLogTailer>(_tailer);
        services.AddSingleton<IServerInstanceService>(_serverService);
        services.AddSingleton(Substitute.For<ILogger<ProcessWatchdogService>>());
        _provider = services.BuildServiceProvider();

        _sut = new ProcessWatchdogService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _processManager,
            _broadcaster,
            _tailer,
            Substitute.For<ILogger<ProcessWatchdogService>>());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
    }

    private async Task<ServerInstance> SeedRunningInstanceAsync(int processId = 999, RestartPolicy policy = RestartPolicy.None, int maxAttempts = 3)
    {
        var instance = new ServerInstance
        {
            Name = "Watchdog Server",
            InstallPath = "/tmp/a3",
            Status = ServerInstanceStatus.Running,
            ProcessId = processId,
            StartedAt = DateTime.UtcNow,
            RestartPolicy = policy,
            MaxRestartAttempts = maxAttempts
        };
        _db.ServerInstances.Add(instance);
        await _db.SaveChangesAsync();
        return instance;
    }

    [Fact]
    public async Task DeadProcess_MarksInstanceCrashed()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        _processManager.IsProcessRunning(999).Returns(false);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Crashed, instance.Status);
        Assert.Null(instance.ProcessId);
        Assert.Null(instance.StartedAt);
    }

    [Fact]
    public async Task AliveProcess_LeavesInstanceUntouched()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        _processManager.IsProcessRunning(999).Returns(true);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(999, instance.ProcessId);
    }

    [Fact]
    public async Task RestartPolicyNone_LeavesCrashedAndClearsCounter()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.None);
        _processManager.IsProcessRunning(999).Returns(false);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Crashed, instance.Status);
        await _serverService.DidNotReceive().StartInstanceAsync(instance.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExceededRestartAttempts_LeavesCrashed()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.OnCrash, maxAttempts: 1);
        _processManager.IsProcessRunning(999).Returns(false);
        _restartSucceeds = false;

        // Attempt 1 fails
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);
        // Attempt 2 exceeds the budget of 1
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Crashed, instance.Status);
        Assert.Null(instance.ProcessId);
    }

    [Fact]
    public async Task ManualStart_ClearsStaleRestartCounter()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.OnCrash, maxAttempts: 1);
        _processManager.IsProcessRunning(999).Returns(false);
        _restartSucceeds = false;

        // Crash once with a failing restart: consumes the budget of 1.
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);
        Assert.Equal(ServerInstanceStatus.Crashed, instance.Status);

        // Simulate a manual start: the instance is Running again with a new PID.
        instance.Status = ServerInstanceStatus.Running;
        instance.ProcessId = 1000;
        instance.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _processManager.IsProcessRunning(1000).Returns(true);

        // The watchdog observes the running instance and clears the stale counter.
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        // The instance crashes again — with the counter cleared, the restart is
        // allowed even though the budget was previously exhausted.
        _restartSucceeds = true;
        _processManager.IsProcessRunning(1000).Returns(false);
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(7777, instance.ProcessId);
    }
}
