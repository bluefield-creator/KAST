namespace KAST.Infrastructure.Steam;

/// <summary>
/// Thrown when a fully downloaded depot file fails SHA-1 verification against
/// its manifest hash. Derives from <see cref="IOException"/> so existing
/// callers that treat download failures as IO errors keep working.
/// </summary>
internal sealed class ChunkHashMismatchException(string message) : IOException(message);
