using System.Net.Sockets;
using System.Text;
using KAST.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace KAST.Infrastructure.Services;

/// <summary>
/// Minimal Valve A2S_INFO client (no external dependency). Handles the
/// S2C_CHALLENGE handshake introduced for amplification protection and parses
/// just the fields KAST surfaces (name, map, players, max players, version).
/// Protocol: https://developer.valvesoftware.com/wiki/Server_queries
/// </summary>
public sealed class A2sServerQueryService(ILogger<A2sServerQueryService> logger) : IServerQueryService
{
    private static readonly byte[] InfoRequestPrefix =
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x54, // header + A2S_INFO
        .. Encoding.ASCII.GetBytes("Source Engine Query"), 0x00
    ];

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);

    public async Task<ServerQueryResult?> QueryAsync(string host, int queryPort, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, queryPort);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResponseTimeout);

            var request = InfoRequestPrefix;
            // At most two challenge rounds: request → challenge → request+challenge → info
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await udp.SendAsync(request, timeoutCts.Token);
                var response = await udp.ReceiveAsync(timeoutCts.Token);
                var payload = response.Buffer;

                if (payload.Length < 5 || BitConverter.ToInt32(payload, 0) != -1)
                    return null; // split packets / garbage — not expected for A2S_INFO

                switch (payload[4])
                {
                    case 0x41 when payload.Length >= 9: // S2C_CHALLENGE
                        request = [.. InfoRequestPrefix, payload[5], payload[6], payload[7], payload[8]];
                        continue;
                    case 0x49: // A2S_INFO reply
                        return ParseInfoResponse(payload.AsSpan(5));
                    default:
                        return null;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // response timeout
        }
        catch (SocketException)
        {
            return null; // port closed / ICMP unreachable
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A2S query against port {Port} failed", queryPort);
            return null;
        }
    }

    /// <summary>Parses an A2S_INFO payload (after the 0x49 type byte).</summary>
    internal static ServerQueryResult? ParseInfoResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            var offset = 0;
            offset++; // protocol version byte

            var name = ReadNullTerminatedString(payload, ref offset);
            var map = ReadNullTerminatedString(payload, ref offset);
            ReadNullTerminatedString(payload, ref offset); // folder
            ReadNullTerminatedString(payload, ref offset); // game

            if (payload.Length < offset + 5)
                return null;

            offset += 2; // steam app id (short)
            var players = payload[offset++];
            var maxPlayers = payload[offset++];
            offset++; // bots

            // server type, environment, visibility, VAC precede the version string
            string version = "";
            if (payload.Length > offset + 4)
            {
                offset += 4;
                version = ReadNullTerminatedString(payload, ref offset);
            }

            return new ServerQueryResult(name, map, players, maxPlayers, version);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // truncated packet
        }
    }

    private static string ReadNullTerminatedString(ReadOnlySpan<byte> payload, ref int offset)
    {
        var terminator = payload[offset..].IndexOf((byte)0);
        if (terminator < 0)
            throw new ArgumentOutOfRangeException(nameof(payload), "Unterminated string in A2S payload.");

        var value = Encoding.UTF8.GetString(payload.Slice(offset, terminator));
        offset += terminator + 1;
        return value;
    }
}
