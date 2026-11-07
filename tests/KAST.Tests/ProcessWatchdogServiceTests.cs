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

        _serverService.GetInstanceByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ServerInstance?>(_db.ServerInstances.FirstOrDefault(i => i.Id == call.Arg<int>())));

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

    [Theory]
    [InlineData(ServerInstanceStatus.Starting, ServerInstanceStatus.Stopped)]
    [InlineData(ServerInstanceStatus.Stopping, ServerInstanceStatus.Stopped)]
    [InlineData(ServerInstanceStatus.Restarting, ServerInstanceStatus.Crashed)]
    public async Task StrandedTransitionalState_WithDeadProcess_IsResolved(ServerInstanceStatus stuck, ServerInstanceStatus expected)
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        instance.Status = stuck;
        await _db.SaveChangesAsync();
        _processManager.IsProcessRunning(999).Returns(false);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(expected, instance.Status);
        Assert.Null(instance.ProcessId);
    }

    [Fact]
    public async Task StrandedStopping_WithLiveProcess_ResolvesToRunning()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        instance.Status = ServerInstanceStatus.Stopping;
        await _db.SaveChangesAsync();
        _processManager.IsProcessRunning(999).Returns(true);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(999, instance.ProcessId);
    }

    [Fact]
    public async Task TransitionalState_WithOperationInFlight_IsLeftAlone()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        instance.Status = ServerInstanceStatus.Stopping;
        await _db.SaveChangesAsync();
        _processManager.IsProcessRunning(999).Returns(false);

        using (await KAST.Infrastructure.Services.InstanceLifecycleLocks.EnterAsync(instance.Id))
            await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        Assert.Equal(ServerInstanceStatus.Stopping, instance.Status);
    }

    [Fact]
    public async Task CrashRecordedByExitHandler_TriggersRestartPolicy()
    {
        // The exit handler marks the row Crashed before the watchdog runs, so
        // the old "Running with dead PID" check never saw it and OnCrash never fired.
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.OnCrash);
        instance.Status = ServerInstanceStatus.Crashed;
        instance.ProcessId = null;
        _db.ServerInstanceProcessHistories.Add(new ServerInstanceProcessHistory
        {
            ServerInstanceId = instance.Id,
            ProcessId = 999,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            EndedAt = DateTime.UtcNow,
            ExitCode = 3,
            TerminationReason = "Crashed"
        });
        await _db.SaveChangesAsync();

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        await _serverService.Received(1).StartInstanceAsync(instance.Id, Arg.Any<CancellationToken>());
        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
    }

    [Fact]
    public async Task IntentionalStop_IsNotRestarted()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.Always);
        instance.Status = ServerInstanceStatus.Stopped;
        instance.ProcessId = null;
        _db.ServerInstanceProcessHistories.Add(new ServerInstanceProcessHistory
        {
            ServerInstanceId = instance.Id,
            ProcessId = 999,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            EndedAt = DateTime.UtcNow,
            TerminationReason = "Stopped"
        });
        await _db.SaveChangesAsync();

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        await _serverService.DidNotReceive().StartInstanceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrashLoop_IsBoundedByMaxRestartAttempts()
    {
        // Each auto-restart "succeeds" but the server dies again. The counter
        // used to reset on every successful start, so this looped forever.
        var instance = await SeedRunningInstanceAsync(processId: 999, policy: RestartPolicy.OnCrash, maxAttempts: 2);
        _processManager.IsProcessRunning(Arg.Any<int>()).Returns(false);

        for (var sweep = 0; sweep < 6; sweep++)
            await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        await _serverService.Received(2).StartInstanceAsync(instance.Id, Arg.Any<CancellationToken>());
        Assert.Equal(ServerInstanceStatus.Crashed, instance.Status);
    }

    [Fact]
    public async Task Sweep_HuntsOrphanedProcesses()
    {
        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        await _serverService.Received(1).ReapOrphanedProcessesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrashedServer_TakesItsHeadlessClientsDown()
    {
        var instance = await SeedRunningInstanceAsync(processId: 999);
        _db.HeadlessClients.Add(new HeadlessClient { ServerInstanceId = instance.Id, ProcessId = 1001, Status = ServerInstanceStatus.Running });
        await _db.SaveChangesAsync();
        _processManager.IsProcessRunning(999).Returns(false);
        _processManager.IsProcessRunning(1001).Returns(true);

        await _sut.CheckRunningInstancesAsync(CancellationToken.None);

        await _processManager.Received(1).KillProcessAsync(1001, Arg.Any<CancellationToken>());
        Assert.All(_db.HeadlessClients, hc => Assert.Null(hc.ProcessId));
    }
}
