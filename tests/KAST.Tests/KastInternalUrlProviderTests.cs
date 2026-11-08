using KAST.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

namespace KAST.Tests;

public class KastInternalUrlProviderTests
{
    private const string PublicBase = "https://panel.example/";

    [Theory]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/hubs/monitoring")]
    [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000/hubs/monitoring")]
    public void GetMonitoringHubUri_UsesConfiguredInternalBaseUrl(string baseUrl, string expected)
    {
        var provider = CreateProvider(
            new Dictionary<string, string?> { ["KAST:InternalBaseUrl"] = baseUrl },
            "http://[::]:5000");

        var uri = provider.GetMonitoringHubUri();

        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("http://[::]:5000", "http://127.0.0.1:5000/hubs/monitoring")]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080/hubs/monitoring")]
    [InlineData("http://+:5000", "http://127.0.0.1:5000/hubs/monitoring")]
    [InlineData("http://localhost:5000", "http://localhost:5000/hubs/monitoring")]
    public void GetMonitoringHubUri_PrefersLocalHttpListenerOverPublicUrl(string listener, string expected)
    {
        var provider = CreateProvider(null, listener);

        var uri = provider.GetMonitoringHubUri();

        Assert.Equal(expected, uri.ToString());
    }

    [Fact]
    public void GetMonitoringHubUri_SkipsHttpsListenerWhenHttpIsAvailable()
    {
        var provider = CreateProvider(null, "https://[::]:5001", "http://[::]:5000");

        var uri = provider.GetMonitoringHubUri();

        Assert.Equal("http://127.0.0.1:5000/hubs/monitoring", uri.ToString());
    }

    [Fact]
    public void GetMonitoringHubUri_FallsBackToPublicUrlForHttpsOnlyListeners()
    {
        var provider = CreateProvider(null, "https://[::]:5001");

        var uri = provider.GetMonitoringHubUri();

        Assert.Equal("https://panel.example/hubs/monitoring", uri.ToString());
    }

    [Fact]
    public void GetMonitoringHubUri_FallsBackToNavigationManagerWhenNoListenerOrConfig()
    {
        var provider = CreateProvider(null);

        var uri = provider.GetMonitoringHubUri();

        Assert.Equal("https://panel.example/hubs/monitoring", uri.ToString());
    }

    [Fact]
    public void SelectLocalListenerBaseUri_IgnoresUnixSocketsAndGarbage()
    {
        var result = KastInternalUrlProvider.SelectLocalListenerBaseUri(
            ["http://unix:/run/kast.sock", "not a url", "http://127.0.0.1:5000"]);

        Assert.Equal("http://127.0.0.1:5000/", result?.ToString());
    }

    private static KastInternalUrlProvider CreateProvider(
        Dictionary<string, string?>? settings,
        params string[] listenerAddresses)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();
        var navigationManager = new TestNavigationManager(PublicBase, PublicBase + "servers");
        return new KastInternalUrlProvider(configuration, navigationManager, new FakeServer(listenerAddresses));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri)
            => Initialize(baseUri, uri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(IEnumerable<string> addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
                feature.Addresses.Add(address);
            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
