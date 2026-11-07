using KAST.UI.Components.Shared;
using MudBlazor;

namespace KAST.UI.Helpers;

public static class DialogServiceExtensions
{
    /// <summary>Shows a confirmation dialog; true when the user confirmed.</summary>
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message,
        string confirmText = "Confirm", bool destructive = false)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { d => d.Title, title },
            { d => d.Message, message },
            { d => d.ConfirmText, confirmText },
            { d => d.Destructive, destructive },
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, CloseOnEscapeKey = true };
        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, options);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
