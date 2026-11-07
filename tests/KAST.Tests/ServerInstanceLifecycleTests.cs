using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using KAST.Infrastructure.Services;
using KAST.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace KAST.Tests;

/// <summary>
/// Regression tests for the stuck Running/Stopping states and orphaned Arma
/// processes reported on 2.0.2.
/// </summary>
public class ServerInstanceLifecycleTests : IDisposable
{
    private const string InstallPath = "/tmp/kast-lifecycle";

    private readonly KastDbContext _db;
    private readonly IProcessManagerService _processManager;
    private readonly IAppEventBroadcaster _broadcaster;
    private readonly ServerInstanceService _sut;

    public ServerInstanceLifecycleTests()
    {
        _db = DbHelper.CreateInMemoryDb();
        _processManager = Substitute.For<IProcessManagerService>();
        _processManager.FindKastServerProcesses().Returns([]);
        _broadcaster = Substitute.For<IAppEventBroadcaster>();
        _sut = new ServerInstanceService(_db, _processManager, _broadcaster,
            Substitute.For<ILogger<ServerInstanceService>>(), new OutputSanitizer(),
            Substitute.For<IServiceScopeFactory>(), Substitute.For<IServerConsoleLogTailer>());
    }

    public void Dispose() => _db.Dispose();

    private static string OwnedExecutable
        => Path.Combine(Path.GetFullPath(InstallPath),
            OperatingSystem.IsWindows() ? "arma3server_x64.exe" : "arma3server_x64");

    private async Task<ServerInstance> SeedAsync(ServerInstanceStatus status, int? pid = null, int headlessClients = 0)
    {
        var instance = new ServerInstance
        {
            Name = "Lifecycle",
            InstallPath = InstallPath,
            Port = 2302,
            Status = status,
            ProcessId = pid
        };
        for (var i = 0; i < headlessClients; i++)
            instance.HeadlessClients.Add(new HeadlessClient());
        _db.ServerInstances.Add(instance);
        await _db.SaveChangesAsync();
        return instance;
    }

    private void LaunchReturns(params int[] pids)
        => _processManager.StartServerProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Action<int, string>?>(), Arg.Any<Action<int, int>?>(), Arg.Any<CancellationToken>())
            .Returns(pids[0], pids.Skip(1).ToArray());

    // ── Start ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ServerInstanceStatus.Running)]
    [InlineData(ServerInstanceStatus.Starting)]
    [InlineData(ServerInstanceStatus.Stopping)]
    [InlineData(ServerInstanceStatus.Restarting)]
    public async Task Start_StaleStateWithDeadProcess_StartsFresh(ServerInstanceStatus stale)
    {
        // Previously Start silently returned for these states, leaving admins
        // unable to start the server until the panel was restarted.
        var instance = await SeedAsync(stale, pid: 111);
        _processManager.IsProcessRunning(111).Returns(false);
        LaunchReturns(222);

        await _sut.StartInstanceAsync(instance.Id);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(222, instance.ProcessId);
    }

    [Fact]
    public async Task Start_StrandedStoppingWithLiveProcess_HealsToRunningWithoutLaunching()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Stopping, pid: 111);
        _processManager.IsProcessRunning(111).Returns(true);

        await _sut.StartInstanceAsync(instance.Id);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(111, instance.ProcessId);
        await _processManager.DidNotReceiveWithAnyArgs()
            .StartServerProcessAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Start_WhileDownloading_ThrowsInsteadOfSilentlyIgnoring()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Downloading);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.StartInstanceAsync(instance.Id));

        Assert.Contains("install", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_HeadlessClientLaunchFails_ServerStaysTrackedAndRunning()
    {
        // A failure after the server launched used to mark it Crashed with the
        // PID unsaved — the running server became an untracked orphan.
        var instance = await SeedAsync(ServerInstanceStatus.Stopped, headlessClients: 1);
        var calls = 0;
        _processManager.StartServerProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Action<int, string>?>(), Arg.Any<Action<int, int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.FromResult(500) : Task.FromException<int>(new IOException("hc failed")));

        await _sut.StartInstanceAsync(instance.Id);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(500, instance.ProcessId);
        var hc = Assert.Single(instance.HeadlessClients);
        Assert.Equal(ServerInstanceStatus.Crashed, hc.Status);
        Assert.Null(hc.ProcessId);
    }

    [Fact]
    public async Task Start_KillsLeftoverTaggedProcessBeforeLaunch()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Stopped);
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(900, instance.Id, null, OwnedExecutable, "-name=server_x")]);
        LaunchReturns(901);

        await _sut.StartInstanceAsync(instance.Id);

        await _processManager.Received(1).KillProcessAsync(900, Arg.Any<CancellationToken>());
        Assert.Equal(901, instance.ProcessId);
    }

    [Fact]
    public async Task Start_CancelledAfterLaunch_StillRecordsProcess()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Stopped);
        using var cts = new CancellationTokenSource();
        _processManager.StartServerProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Action<int, string>?>(), Arg.Any<Action<int, int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel(); // e.g. the browser tab closed right after launch
                return Task.FromResult(321);
            });

        await _sut.StartInstanceAsync(instance.Id, cts.Token);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(321, instance.ProcessId);
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_CallerCancelsMidStop_StillReachesStopped()
    {
        // An aborted request / closed tab used to cancel the stop after
        // Stopping was saved, stranding the instance in Stopping forever.
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        using var cts = new CancellationTokenSource();
        _processManager.StopProcessAsync(42, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cts.Cancel();
                return call.Arg<CancellationToken>().IsCancellationRequested
                    ? Task.FromCanceled(call.Arg<CancellationToken>())
                    : Task.CompletedTask;
            });

        await _sut.StopInstanceAsync(instance.Id, cts.Token);

        Assert.Equal(ServerInstanceStatus.Stopped, instance.Status);
        Assert.Null(instance.ProcessId);
    }

    [Fact]
    public async Task Stop_ProcessSurvivesKill_ReportsRunningNotStopping()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        _processManager.StopProcessAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("survived kill"));
        _processManager.IsProcessRunning(42).Returns(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.StopInstanceAsync(instance.Id));

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(42, instance.ProcessId);
    }

    [Fact]
    public async Task Stop_KillsUntrackedTaggedProcessesOfInstance()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        _processManager.FindKastServerProcesses().Returns(
        [
            new KastProcessInfo(42, instance.Id, null, OwnedExecutable, null),
            new KastProcessInfo(43, instance.Id, null, OwnedExecutable, null),
            new KastProcessInfo(44, instance.Id, 1, OwnedExecutable, null)
        ]);

        await _sut.StopInstanceAsync(instance.Id);

        await _processManager.Received(1).StopProcessAsync(42, Arg.Any<CancellationToken>());
        await _processManager.Received(1).KillProcessAsync(43, Arg.Any<CancellationToken>());
        await _processManager.Received(1).KillProcessAsync(44, Arg.Any<CancellationToken>());
        Assert.Equal(ServerInstanceStatus.Stopped, instance.Status);
    }

    [Fact]
    public async Task Stop_NeverKillsTaggedProcessFromAnotherInstallDirectory()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        var foreignExe = Path.Combine(Path.GetFullPath("/elsewhere/arma"), "arma3server_x64.exe");
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(77, instance.Id, null, foreignExe, null),
             new KastProcessInfo(78, instance.Id, null, null, null)]);

        await _sut.StopInstanceAsync(instance.Id);

        await _processManager.DidNotReceive().KillProcessAsync(77, Arg.Any<CancellationToken>());
        await _processManager.DidNotReceive().KillProcessAsync(78, Arg.Any<CancellationToken>());
    }

    // ── Restart ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restart_StartFails_DoesNotLeaveRestarting()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        _processManager.StartServerProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Action<int, string>?>(), Arg.Any<Action<int, int>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("exe missing"));

        await Assert.ThrowsAsync<IOException>(() => _sut.RestartInstanceAsync(instance.Id));

        Assert.Equal(ServerInstanceStatus.Stopped, instance.Status);
        Assert.Null(instance.ProcessId);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAndStop_AreSerializedPerInstance()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 42);
        var stopEntered = new TaskCompletionSource();
        var releaseStop = new TaskCompletionSource();
        _processManager.StopProcessAsync(42, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                stopEntered.SetResult();
                await releaseStop.Task;
            });
        LaunchReturns(55);

        var stop = _sut.StopInstanceAsync(instance.Id);
        await stopEntered.Task;
        var start = _sut.StartInstanceAsync(instance.Id);

        await Task.Delay(100);
        Assert.False(start.IsCompleted); // waits for the stop instead of racing it

        releaseStop.SetResult();
        await Task.WhenAll(stop, start);

        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        Assert.Equal(55, instance.ProcessId);
    }

    // ── Process hunting ──────────────────────────────────────────────────────

    [Fact]
    public async Task Reap_RunningRowWithLostPid_AdoptsLiveServer()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Running, pid: 10);
        _processManager.IsProcessRunning(10).Returns(false);
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(20, instance.Id, null, OwnedExecutable, null)]);

        var handled = await _sut.ReapOrphanedProcessesAsync();

        Assert.Equal(1, handled);
        Assert.Equal(20, instance.ProcessId);
        Assert.Equal(ServerInstanceStatus.Running, instance.Status);
        await _processManager.DidNotReceive().KillProcessAsync(20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reap_StoppedInstanceWithTaggedProcess_KillsIt()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Stopped);
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(30, instance.Id, null, OwnedExecutable, null)]);
        _processManager.KillProcessAsync(30, Arg.Any<CancellationToken>()).Returns(true);

        var handled = await _sut.ReapOrphanedProcessesAsync();

        Assert.Equal(1, handled);
        await _processManager.Received(1).KillProcessAsync(30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reap_UnknownInstance_LeavesProcessAlone()
    {
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(31, 9999, null, OwnedExecutable, null)]);

        var handled = await _sut.ReapOrphanedProcessesAsync();

        Assert.Equal(0, handled);
        await _processManager.DidNotReceiveWithAnyArgs().KillProcessAsync(default, default);
    }

    [Fact]
    public async Task Reap_InstanceBusy_IsSkipped()
    {
        var instance = await SeedAsync(ServerInstanceStatus.Stopped);
        _processManager.FindKastServerProcesses().Returns(
            [new KastProcessInfo(32, instance.Id, null, OwnedExecutable, null)]);

        using (await InstanceLifecycleLocks.EnterAsync(instance.Id))
        {
            var handled = await _sut.ReapOrphanedProcessesAsync();
            Assert.Equal(0, handled);
        }

        await _processManager.DidNotReceiveWithAnyArgs().KillProcessAsync(default, default);
    }
}
