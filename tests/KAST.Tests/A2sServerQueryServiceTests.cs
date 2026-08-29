using System.Net;
using System.Net.Sockets;
using System.Text;
using KAST.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KAST.Tests;

public class A2sServerQueryServiceTests
{
    private static byte[] BuildInfoPayload(
        string name = "KAST Test Server",
        string map = "Altis",
        byte players = 12,
        byte maxPlayers = 64,
        string version = "2.18.152000")
    {
        var ms = new MemoryStream();
        ms.WriteByte(17); // protocol version
        WriteString(ms, name);
        WriteString(ms, map);
        WriteString(ms, "Arma3");     // folder
        WriteString(ms, "Arma 3");    // game
        ms.Write(BitConverter.GetBytes(unchecked((short)107410))); // app id (truncated to short per protocol)
        ms.WriteByte(players);
        ms.WriteByte(maxPlayers);
        ms.WriteByte(0);   // bots
        ms.WriteByte((byte)'d'); // server type
        ms.WriteByte((byte)'w'); // environment
        ms.WriteByte(1);   // visibility
        ms.WriteByte(0);   // VAC
        WriteString(ms, version);
        return ms.ToArray();

        static void WriteString(MemoryStream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }
    }

    [Fact]
    public void ParseInfoResponse_ValidPayload_ExtractsFields()
    {
        var result = A2sServerQueryService.ParseInfoResponse(BuildInfoPayload());

        Assert.NotNull(result);
        Assert.Equal("KAST Test Server", result!.ServerName);
        Assert.Equal("Altis", result.Map);
        Assert.Equal(12, result.PlayerCount);
        Assert.Equal(64, result.MaxPlayers);
        Assert.Equal("2.18.152000", result.GameVersion);
    }

    [Fact]
    public void ParseInfoResponse_TruncatedPayload_ReturnsNull()
    {
        var full = BuildInfoPayload();
        var truncated = full.AsSpan(0, 10);

        Assert.Null(A2sServerQueryService.ParseInfoResponse(truncated));
    }

    [Fact]
    public void ParseInfoResponse_EmptyPayload_ReturnsNull()
    {
        Assert.Null(A2sServerQueryService.ParseInfoResponse([1, 0]));
    }

    [Fact]
    public async Task QueryAsync_NoServerListening_ReturnsNullWithinTimeout()
    {
        var sut = new A2sServerQueryService(NullLogger<A2sServerQueryService>.Instance);

        // An ephemeral port nothing listens on
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await sut.QueryAsync("127.0.0.1", 1);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "query must fail fast, not hang");
    }

    [Fact]
    public async Task QueryAsync_ServerAnswersDirectly_ReturnsInfo()
    {
        using var responder = new FakeA2sResponder(useChallenge: false);
        var sut = new A2sServerQueryService(NullLogger<A2sServerQueryService>.Instance);

        var result = await sut.QueryAsync("127.0.0.1", responder.Port);

        Assert.NotNull(result);
        Assert.Equal(12, result!.PlayerCount);
        Assert.Equal(64, result.MaxPlayers);
    }

    [Fact]
    public async Task QueryAsync_ServerRequiresChallenge_ReturnsInfo()
    {
        using var responder = new FakeA2sResponder(useChallenge: true);
        var sut = new A2sServerQueryService(NullLogger<A2sServerQueryService>.Instance);

        var result = await sut.QueryAsync("127.0.0.1", responder.Port);

        Assert.NotNull(result);
        Assert.Equal("Altis", result!.Map);
    }

    private sealed class FakeA2sResponder : IDisposable
    {
        private static readonly byte[] Challenge = [0xAA, 0xBB, 0xCC, 0xDD];
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public FakeA2sResponder(bool useChallenge)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

            _ = Task.Run(async () =>
            {
                var challengeIssued = false;
                while (!_cts.IsCancellationRequested)
                {
                    var request = await _udp.ReceiveAsync(_cts.Token);

                    byte[] reply;
                    if (useChallenge && !challengeIssued)
                    {
                        challengeIssued = true;
                        reply = [0xFF, 0xFF, 0xFF, 0xFF, 0x41, .. Challenge];
                    }
                    else
                    {
                        reply = [0xFF, 0xFF, 0xFF, 0xFF, 0x49, .. BuildInfoPayload()];
                    }

                    await _udp.SendAsync(reply, request.RemoteEndPoint, _cts.Token);
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _cts.Dispose();
        }
    }
}
