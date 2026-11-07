using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace KAST.UI.Services;

public enum ThemeMode
{
    Dark,
    Light,
    System,
}

/// <summary>
/// Per-browser colour mode. Dark is the default; System follows the OS
/// preference reported by the layout's MudThemeProvider.
/// </summary>
public class ThemeService(ProtectedLocalStorage storage)
{
    private const string ModeKey = "caster_theme_mode";

    // Keys written by the pre-2.1 theme (accent swatches, dark toggle).
    private static readonly string[] LegacyKeys = ["kast_accent_color", "kast_dark_mode"];

    private bool _initialized;
    private bool _systemPrefersDark = true;

    public ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    public bool IsDarkMode => Mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.System => _systemPrefersDark,
        _ => true,
    };

    public event Action? OnChange;

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            var stored = await storage.GetAsync<string>(ModeKey);
            if (stored.Success && Enum.TryParse<ThemeMode>(stored.Value, ignoreCase: true, out var mode))
                Mode = mode;

            foreach (var key in LegacyKeys)
                await storage.DeleteAsync(key);
        }
        catch
        {
            // Prerendering or storage unavailable: keep the default.
        }

        OnChange?.Invoke();
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        Mode = mode;
        try { await storage.SetAsync(ModeKey, mode.ToString()); } catch { }
        OnChange?.Invoke();
    }

    /// <summary>Called by the layout with the OS colour-scheme preference.</summary>
    public void SetSystemPreference(bool prefersDark)
    {
        if (_systemPrefersDark == prefersDark)
            return;
        _systemPrefersDark = prefersDark;
        if (Mode == ThemeMode.System)
            OnChange?.Invoke();
    }
}
