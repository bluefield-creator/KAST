namespace KAST.Core.Exceptions;

/// <summary>
/// Thrown when a requested entity does not exist. Derives from
/// <see cref="InvalidOperationException"/> so existing callers that catch the
/// broader type keep working; the API exception filter maps it to 404.
/// </summary>
public class NotFoundException(string message) : InvalidOperationException(message);
