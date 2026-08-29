namespace KAST.Core.Interfaces;

/// <summary>Result of a Steam A2S_INFO query against a game server.</summary>
public sealed record ServerQueryResult(
    string ServerName,
    string Map,
    int PlayerCount,
    int MaxPlayers,
    string GameVersion);

/// <summary>
/// Queries a running game server over the Steam query protocol (A2S).
/// </summary>
public interface IServerQueryService
{
    /// <summary>
    /// Sends an A2S_INFO query. Returns <c>null</c> when the server does not
    /// answer within the timeout (down, still booting, or port blocked) —
    /// never throws for an unreachable server.
    /// </summary>
    Task<ServerQueryResult?> QueryAsync(string host, int queryPort, CancellationToken ct = default);
}
