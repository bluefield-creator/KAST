using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KAST.UI.Services;

public sealed class KastInternalUrlProvider
{
    private const string MonitoringHubPath = "hubs/monitoring";

    private static readonly HashSet<string> WildcardHosts =
        new(StringComparer.OrdinalIgnoreCase) { "+", "*", "0.0.0.0", "::", "[::]" };

    private readonly IConfiguration _configuration;
    private readonly NavigationManager _navigationManager;
    private readonly IServer _server;

    public KastInternalUrlProvider(
        IConfiguration configuration,
        NavigationManager navigationManager,
        IServer server)
    {
        _configuration = configuration;
        _navigationManager = navigationManager;
        _server = server;
    }

    public Uri GetMonitoringHubUri()
    {
        var internalBaseUrl = _configuration["KAST:InternalBaseUrl"];

        if (!string.IsNullOrWhiteSpace(internalBaseUrl))
        {
            return new Uri(
                new Uri(internalBaseUrl.TrimEnd('/') + "/"),
                MonitoringHubPath);
        }

        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var localBase = SelectLocalListenerBaseUri(addresses);
        if (localBase is not null)
            return new Uri(localBase, MonitoringHubPath);

        return _navigationManager.ToAbsoluteUri("/" + MonitoringHubPath);
    }
    public static Uri? SelectLocalListenerBaseUri(IEnumerable<string>? addresses)
    {
        if (addresses is null)
            return null;

        foreach (var address in addresses)
        {
            BindingAddress binding;
            try
            {
                binding = BindingAddress.Parse(address);
            }
            catch (FormatException)
            {
                continue;
            }

            if (binding.IsUnixPipe ||
                binding.Port <= 0 ||
                !string.Equals(binding.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var host = WildcardHosts.Contains(binding.Host) ? "127.0.0.1" : binding.Host;
            var path = binding.PathBase.TrimEnd('/') + "/";
            return new UriBuilder(Uri.UriSchemeHttp, host, binding.Port, path).Uri;
        }

        return null;
    }
}
