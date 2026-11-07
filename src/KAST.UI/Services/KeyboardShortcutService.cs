using Microsoft.JSInterop;

namespace KAST.UI.Services;

public class KeyboardShortcutService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly Dictionary<string, Func<Task>> _shortcuts = new();
    private DotNetObjectReference<KeyboardShortcutService>? _dotNetRef;
    private bool _initialized;

    public KeyboardShortcutService(IJSRuntime js) => _js = js;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _dotNetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("KAST.keyboard.init", _dotNetRef);
        _initialized = true;
    }

    /// <summary>Raised by Ctrl+K / ⌘K and by the app-bar search button.</summary>
    public event Action? OpenCommandPalette;

    public void RequestCommandPalette() => OpenCommandPalette?.Invoke();

    public void Register(string combo, Func<Task> handler)
    {
        _shortcuts[combo.ToLower()] = handler;
    }

    [JSInvokable]
    public async Task OnKeyDown(string combo)
    {
        if (combo.Equals("ctrl+k", StringComparison.OrdinalIgnoreCase))
        {
            RequestCommandPalette();
            return;
        }

        if (_shortcuts.TryGetValue(combo.ToLower(), out var handler))
            await handler();
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await _js.InvokeVoidAsync("KAST.keyboard.dispose");
            }
            catch (JSDisconnectedException)
            {
                _initialized = false;
            }
            catch (InvalidOperationException)
            {
                _initialized = false;
            }
        }
        _dotNetRef?.Dispose();
    }
}
