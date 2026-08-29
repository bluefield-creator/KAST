using KAST.UI.Services;
using Microsoft.AspNetCore.Http;

namespace KAST.Tests;

public class AccountGateTests
{
    private static HttpRequest CreateRequest(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context.Request;
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/ready")]
    [InlineData("/login")]
    [InlineData("/auth/login")]
    public void IsAnonymousRequest_AllowsProbeAndAuthPaths(string path)
    {
        Assert.True(AccountGateMiddlewareExtensions.IsAnonymousRequest(CreateRequest(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/servers")]
    [InlineData("/api/servers")]
    public void IsAnonymousRequest_RejectsProtectedPaths(string path)
    {
        Assert.False(AccountGateMiddlewareExtensions.IsAnonymousRequest(CreateRequest(path)));
    }
}
