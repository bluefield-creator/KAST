using KAST.Core.Exceptions;
using KAST.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace KAST.UI.Api;

/// <summary>
/// Converts service-layer exceptions escaping /api endpoints into ProblemDetails
/// JSON. Without this, errors re-execute the Razor error page and API callers
/// receive HTML 500s — including for plain not-found conditions.
/// </summary>
public sealed class ApiExceptionFilter(
    IOutputSanitizer sanitizer,
    ILogger<ApiExceptionFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (NotFoundException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: sanitizer.Sanitize(ex.Message));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request",
                detail: sanitizer.Sanitize(ex.Message));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: sanitizer.Sanitize(ex.Message));
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Client went away — nothing useful to return
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled API error on {Path}", context.HttpContext.Request.Path);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal server error",
                detail: sanitizer.Sanitize(ex.Message));
        }
    }
}
