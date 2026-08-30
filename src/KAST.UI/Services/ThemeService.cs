using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.Globalization;
using System.Security.Cryptography;

namespace KAST.UI.Services;

public class ThemeService(ProtectedLocalStorage storage) : IDisposable
{
    private const string AccentKey = "kast_accent_color";
    private const string DarkModeKey = "kast_dark_mode";
    private const string LegacyDefaultAccent = "#5EB1FF"; // pre-Artoria "Castoria azure" default
    public const string DefaultAccent = "#DDA8D6";        // the Staff Ribbon

    private CancellationTokenSource? _cts;
    private bool _userAccentOverride;

    /// <summary>
    /// The ribbon system. Per the forensics dossier, the lilac staff
    /// ribbon is "the one color the uniform did not issue ... the only
    /// visible evidence of a person making a choice." The accent color is
    /// exactly that: the one thing the operator ties on themselves. Every
    /// option is sampled from her — light grades, readable as text on the
    /// indigo canvas. Stored values remain plain hex.
    /// </summary>
    public static readonly IReadOnlyList<(string Value, string Label)> CuratedAccents =
    [
        ("#DDA8D6", "Staff Ribbon"),
        ("#91BDE0", "Cabochon"),
        ("#D5ECB8", "Eye Light"),
        ("#EBCB95", "Cream Braid"),
        ("#FBC9A0", "Apricot"),
        ("#E8A8AE", "Lining Blush"),
    ];

    /// <summary>Hex accent color applied as Secondary palette entry.</summary>
    public string AccentColor { get; private set; } = DefaultAccent;

    /// <summary>
    /// True (default) = Regulation, the night watch. False = Full Dress,
    /// the daylight portrait — garments keep their colors, the ground
    /// turns tunic-white.
    /// </summary>
    public bool IsDarkMode { get; private set; } = true;

    public event Action? OnChange;

    public async Task InitializeAsync()
    {
        try
        {
            var accentResult = await storage.GetAsync<string>(AccentKey);
            if (accentResult.Success && !string.IsNullOrEmpty(accentResult.Value))
            {
                if (string.Equals(accentResult.Value, LegacyDefaultAccent, StringComparison.OrdinalIgnoreCase))
                {
                    // User never left the old default — migrate them to the new one.
                    try { await storage.DeleteAsync(AccentKey); } catch { }
                }
                else
                {
                    AccentColor = SnapToCurated(accentResult.Value);
                    _userAccentOverride = true;
                }
            }

            var darkResult = await storage.GetAsync<bool>(DarkModeKey);
            if (darkResult.Success)
                IsDarkMode = darkResult.Value;
        }
        catch (CryptographicException)
        {
            await TryDeleteThemeStorageAsync();
        }
        catch { /* ignore during pre-render or when storage is unavailable */ }

        StartAprilFoolsIfNeeded();
        OnChange?.Invoke();
    }

    public async Task SetAccentAsync(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return;
        AccentColor = color;
        _userAccentOverride = true;
        try { await storage.SetAsync(AccentKey, color); } catch { }
        OnChange?.Invoke();
    }

    public async Task SetDarkModeAsync(bool isDark)
    {
        IsDarkMode = isDark;
        try { await storage.SetAsync(DarkModeKey, isDark); } catch { }
        OnChange?.Invoke();
    }

    /// <summary>
    /// Maps a stored legacy accent to the nearest curated accent (RGB distance).
    /// Applied only to persisted values, never to runtime cycling.
    /// </summary>
    private static string SnapToCurated(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b))
            return DefaultAccent;

        var best = DefaultAccent;
        var bestDist = int.MaxValue;
        foreach (var (value, _) in CuratedAccents)
        {
            TryParseHex(value, out var cr, out var cg, out var cb);
            var dist = (r - cr) * (r - cr) + (g - cg) * (g - cg) + (b - cb) * (b - cb);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = value;
            }
        }
        return best;
    }

    private static bool TryParseHex(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = hex.TrimStart('#');
        if (s.Length != 6) return false;
        return int.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private void StartAprilFoolsIfNeeded()
    {
        // treat April 1st in local time; use DateTime.Now
        var localNow = DateTime.Now;
        if (localNow is not { Month: 4, Day: 1 }) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = AprilFoolsLoopAsync(_cts.Token);
    }

    private async Task AprilFoolsLoopAsync(CancellationToken ct)
    {
        // Cycle through the curated Artoria accents on April Fools' Day
        var idx = 0;
        try
        {
            while (!ct.IsCancellationRequested && !_userAccentOverride)
            {
                AccentColor = CuratedAccents[idx % CuratedAccents.Count].Value;
                OnChange?.Invoke();
                idx++;
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (TaskCanceledException) { /* Ignore */ }
    }

    private async Task TryDeleteThemeStorageAsync()
    {
        try
        {
            await storage.DeleteAsync(AccentKey);
            await storage.DeleteAsync(DarkModeKey);
        }
        catch { }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;

        _cts?.Cancel();
        _cts?.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
