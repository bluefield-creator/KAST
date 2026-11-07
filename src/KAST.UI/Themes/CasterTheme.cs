using MudBlazor;

namespace KAST.UI.Themes;

/// <summary>
/// CASTER's MudBlazor theme: the only place colours are defined.
/// <para>
/// MudBlazor publishes these values as <c>--mud-palette-*</c> CSS variables;
/// <c>wwwroot/css/caster.css</c> builds its small <c>--k-*</c> layer on top of
/// them, so stylesheets and components never carry their own hex values.
/// </para>
/// <para>
/// Direction: neutral greys for structure, one brand colour (Soviet red) for
/// primary actions, focus and selection, and semantic colours for status only.
/// All text/background pairs meet WCAG 2.2 AA.
/// </para>
/// </summary>
public static class CasterTheme
{
    /// <summary>Soviet red, the brand colour.</summary>
    public const string BrandRed = "#CC0000";

    public static PaletteDark Dark => new()
    {
        // Brand: filled actions. #DA1E28 keeps white button text at 5.0:1;
        // red used as text on dark surfaces is lifted via --k-primary-text.
        Primary                  = "#DA1E28",
        PrimaryContrastText      = "#FFFFFF",
        PrimaryDarken            = "#B3141D",
        PrimaryLighten           = "#FF6B63",
        Secondary                = "#9AA0AC",
        SecondaryContrastText    = "#0F1115",
        Tertiary                 = "#C2C7D0",
        TertiaryContrastText     = "#0F1115",

        Info                     = "#38BDF8",
        InfoContrastText         = "#0F1115",
        Success                  = "#22C55E",
        SuccessContrastText      = "#0F1115",
        Warning                  = "#F59E0B",
        WarningContrastText      = "#0F1115",
        Error                    = "#F87171",
        ErrorContrastText        = "#0F1115",
        Dark                     = "#0B0C0F",
        DarkContrastText         = "#E8EAEE",

        Background               = "#0F1115",
        BackgroundGray           = "#13151A",
        Surface                  = "#16181D",
        AppbarBackground         = "#0B0C0F",
        AppbarText               = "#E8EAEE",
        DrawerBackground         = "#0B0C0F",
        DrawerText               = "#C2C7D0",
        DrawerIcon               = "#9AA0AC",

        TextPrimary              = "#E8EAEE",
        TextSecondary            = "#9AA0AC",
        TextDisabled             = "#6B7280",
        ActionDefault            = "#9AA0AC",
        ActionDisabled           = "#4B5160",
        ActionDisabledBackground = "#1F2229",

        LinesDefault             = "#2A2D35",
        LinesInputs              = "#3A3E48",
        TableLines               = "#23262D",
        TableStriped             = "#1A1C22",
        TableHover               = "#1C1F25",
        Divider                  = "#2A2D35",
        DividerLight             = "#1F2229",
        Skeleton                 = "#1F2229",
        OverlayDark              = "#00000099",
        GrayDefault              = "#6B7280",
        GrayLight                = "#2A2D35",
        GrayLighter              = "#1F2229",
        GrayDark                 = "#9AA0AC",
        GrayDarker               = "#C2C7D0",
        HoverOpacity             = 0.06,
    };

    public static PaletteLight Light => new()
    {
        Primary                  = BrandRed,
        PrimaryContrastText      = "#FFFFFF",
        PrimaryDarken            = "#A30000",
        PrimaryLighten           = "#E53935",
        Secondary                = "#475467",
        SecondaryContrastText    = "#FFFFFF",
        Tertiary                 = "#344054",
        TertiaryContrastText     = "#FFFFFF",

        Info                     = "#0369A1",
        InfoContrastText         = "#FFFFFF",
        Success                  = "#15803D",
        SuccessContrastText      = "#FFFFFF",
        Warning                  = "#B45309",
        WarningContrastText      = "#FFFFFF",
        Error                    = "#B91C1C",
        ErrorContrastText        = "#FFFFFF",
        Dark                     = "#14161A",
        DarkContrastText         = "#FFFFFF",

        Background               = "#F6F7F9",
        BackgroundGray           = "#EEF0F3",
        Surface                  = "#FFFFFF",
        AppbarBackground         = "#FFFFFF",
        AppbarText               = "#14161A",
        DrawerBackground         = "#FFFFFF",
        DrawerText               = "#344054",
        DrawerIcon               = "#5B616E",

        TextPrimary              = "#14161A",
        TextSecondary            = "#5B616E",
        TextDisabled             = "#9CA3AF",
        ActionDefault            = "#5B616E",
        ActionDisabled           = "#B4BAC4",
        ActionDisabledBackground = "#EEF0F3",

        LinesDefault             = "#E3E5EA",
        LinesInputs              = "#CDD1D8",
        TableLines               = "#EDEFF2",
        TableStriped             = "#F9FAFB",
        TableHover               = "#F3F4F6",
        Divider                  = "#E3E5EA",
        DividerLight             = "#EEF0F3",
        Skeleton                 = "#EEF0F3",
        OverlayDark              = "#0F111566",
        GrayDefault              = "#9CA3AF",
        GrayLight                = "#E3E5EA",
        GrayLighter              = "#F3F4F6",
        GrayDark                 = "#5B616E",
        GrayDarker               = "#344054",
        HoverOpacity             = 0.04,
    };

    private static readonly string[] Sans = ["Inter", "-apple-system", "Segoe UI", "system-ui", "sans-serif"];

    public static MudTheme Build() => new()
    {
        PaletteLight = Light,
        PaletteDark = Dark,
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "56px",
            DrawerWidthLeft = "240px",
        },
        // Flat UI: borders separate surfaces; only floating layers (menus,
        // dialogs, popovers) keep a shadow.
        Shadows = new Shadow
        {
            Elevation =
            [
                "none",
                "none",
                "none",
                "none",
                "0 4px 12px rgba(0,0,0,.18)",
                "0 4px 12px rgba(0,0,0,.18)",
                "0 4px 12px rgba(0,0,0,.18)",
                "0 4px 12px rgba(0,0,0,.18)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 8px 24px rgba(0,0,0,.24)",
                "0 16px 40px rgba(0,0,0,.32)",
                "0 16px 40px rgba(0,0,0,.32)",
            ],
        },
        Typography = new Typography
        {
            // Type scale: 12 / 13 / 14 / 16 / 20 / 24
            Default   = new DefaultTypography   { FontFamily = Sans, FontSize = ".875rem",  FontWeight = "400", LineHeight = "1.5" },
            H1        = new H1Typography        { FontFamily = Sans, FontSize = "1.5rem",   FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "-.015em" },
            H2        = new H2Typography        { FontFamily = Sans, FontSize = "1.5rem",   FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "-.015em" },
            H3        = new H3Typography        { FontFamily = Sans, FontSize = "1.5rem",   FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "-.015em" },
            H4        = new H4Typography        { FontFamily = Sans, FontSize = "1.5rem",   FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "-.015em" },
            H5        = new H5Typography        { FontFamily = Sans, FontSize = "1.25rem",  FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-.01em" },
            H6        = new H6Typography        { FontFamily = Sans, FontSize = "1rem",     FontWeight = "600", LineHeight = "1.4" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "1rem",     FontWeight = "600", LineHeight = "1.4" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = ".875rem",  FontWeight = "600", LineHeight = "1.4" },
            Body1     = new Body1Typography     { FontFamily = Sans, FontSize = ".875rem",  FontWeight = "400", LineHeight = "1.5" },
            Body2     = new Body2Typography     { FontFamily = Sans, FontSize = ".8125rem", FontWeight = "400", LineHeight = "1.5" },
            Button    = new ButtonTypography    { FontFamily = Sans, FontSize = ".875rem",  FontWeight = "500", LineHeight = "1.5", TextTransform = "none" },
            Caption   = new CaptionTypography   { FontFamily = Sans, FontSize = ".75rem",   FontWeight = "400", LineHeight = "1.4" },
            Overline  = new OverlineTypography  { FontFamily = Sans, FontSize = ".75rem",   FontWeight = "600", LineHeight = "1.4", LetterSpacing = ".04em", TextTransform = "uppercase" },
        },
    };
}
