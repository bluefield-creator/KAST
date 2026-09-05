using MudBlazor;

namespace KAST.UI.Themes;

/// <summary>
/// The Artoria design system's MudBlazor adapter — Blue Kepi edition,
/// built from the visual-forensics dossier's region-mode pixel samples.
/// Single permanent theme; no light mode. Uniform indigo (#3B3A86 family,
/// violet-shifted) is the structure, tunic white carries actions, crimson
/// is lining revealed on state change, brass/cream trace the edges, and
/// the user accent is the staff ribbon — the one unissued color.
/// Canonical token values live in wwwroot/css/artoria-tokens.css — the two
/// files duplicate ~25 hex values and must be kept in sync.
/// Usage rules: docs/DESIGN.md.
/// </summary>
public static class ArtoriaTheme
{
    /// <summary>Inside the coat at night.</summary>
    public static PaletteDark Regulation => new()
    {
        Primary                  = "#FFFFFF",  // the tunic — white carries actions
        PrimaryContrastText      = "#1C1C4A",
        PrimaryDarken            = "#E4E4E4",  // fold
        PrimaryLighten           = "#FFFFFF",
        Secondary                = "#DDA8D6",  // the ribbon — overridden by user accent
        SecondaryContrastText    = "#131331",
        Tertiary                 = "#EBCB95",  // cream piping (trim grade of brass)
        TertiaryContrastText     = "#131331",
        Info                     = "#91BDE0",  // the cabochon stone
        InfoContrastText         = "#131331",
        Success                  = "#8FC3A8",  // her eye-green, text grade
        SuccessContrastText      = "#131331",
        Warning                  = "#DDA389",  // hair-tip apricot — warmth, not gold
        WarningContrastText      = "#131331",
        Error                    = "#DD4F4D",  // lit crimson lining
        ErrorContrastText        = "#131331",
        Background               = "#131331",
        BackgroundGray           = "#161638",
        Surface                  = "#191940",
        AppbarBackground         = "#0E0E24",
        AppbarText               = "#F7F6FA",
        DrawerBackground         = "#0E0E24",
        DrawerText               = "#F7F6FA",
        DrawerIcon               = "#AAA6C9",
        TextPrimary              = "#F7F6FA",
        TextSecondary            = "#AAA6C9",
        TextDisabled             = "#71738D",
        ActionDefault            = "#AAA6C9",
        ActionDisabled           = "#71738D",
        ActionDisabledBackground = "#22225866",
        LinesDefault             = "#34336E",
        TableLines               = "#34336E",
        Divider                  = "#34336E",
        DividerLight             = "#222258",
        OverlayDark              = "#0E0E2480",
        GrayLight                = "#3B3A86",
        GrayLighter              = "#222258",
    };

    /// <summary>
    /// Full Dress — the daylight portrait. The dossier measured her at 55%
    /// white: light mode is the parade ground. Garments (appbar, drawer,
    /// belt headers) stay indigo via CSS; here the coat carries actions —
    /// indigo primary on tunic-white plates, fold shadows leaning violet.
    /// </summary>
    public static PaletteLight FullDress => new()
    {
        Primary                  = "#3B3A86",  // the coat carries actions in daylight
        PrimaryContrastText      = "#FFFFFF",
        PrimaryDarken            = "#24225E",
        PrimaryLighten           = "#5A57A8",
        Secondary                = "#A15497",  // ribbon, daylight grade — overridden by accent
        SecondaryContrastText    = "#FFFFFF",
        Tertiary                 = "#8A6526",  // brass at text grade
        TertiaryContrastText     = "#FFFFFF",
        Info                     = "#33689B",
        InfoContrastText         = "#FFFFFF",
        Success                  = "#2F7263",
        SuccessContrastText      = "#FFFFFF",
        Warning                  = "#A6633C",
        WarningContrastText      = "#FFFFFF",
        Error                    = "#A8142F",
        ErrorContrastText        = "#FFFFFF",
        Background               = "#ECEDF4",
        BackgroundGray           = "#E4E5EF",
        Surface                  = "#FBFBFE",
        AppbarBackground         = "#0E0E24",  // the kepi stays dark at noon
        AppbarText               = "#F7F6FA",
        DrawerBackground         = "#0E0E24",  // so does the capelet
        DrawerText               = "#F7F6FA",
        DrawerIcon               = "#AAA6C9",
        TextPrimary              = "#15162A",
        TextSecondary            = "#4B4D69",
        TextDisabled             = "#9092A9",
        ActionDefault            = "#4B4D69",
        ActionDisabled           = "#9092A9",
        ActionDisabledBackground = "#E4E5EF",
        LinesDefault             = "#C9CBDD",
        TableLines               = "#DCDEE9",
        Divider                  = "#C9CBDD",
        DividerLight             = "#E4E5EF",
        Black                    = "#15162A",
        GrayLight                = "#DCDEE9",
        GrayLighter              = "#F1F1F7",
        HoverOpacity             = 0.05,
    };

    /// <summary>
    /// The ribbon by daylight: the curated accents are night grades; as
    /// caption text on tunic-white they would wash out, so each maps to a
    /// darker same-hue counterpart in Full Dress.
    /// </summary>
    public static string ToDaylightAccent(string accent) => accent.ToUpperInvariant() switch
    {
        "#DDA8D6" => "#A15497",  // Staff Ribbon
        "#91BDE0" => "#2F6598",  // Cabochon
        "#D5ECB8" => "#4C7A3D",  // Eye Light
        "#EBCB95" => "#8A6526",  // Cream Braid
        "#FBC9A0" => "#A6633C",  // Apricot
        "#E8A8AE" => "#A8434E",  // Lining Blush
        _ => "#3B3A86",
    };

    // One face, two optical cuts: Inter carries display and text alike
    // (display sizes tighten their tracking rather than switching faces).
    private static readonly string[] Display = ["Inter", "-apple-system", "SF Pro Display", "Segoe UI", "system-ui", "sans-serif"];
    private static readonly string[] Sans    = ["Inter", "-apple-system", "SF Pro Text", "Segoe UI", "system-ui", "sans-serif"];

    public static MudTheme Build() => new()
    {
        PaletteLight = FullDress,
        PaletteDark = Regulation,
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "3px" },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = Sans,
                FontSize   = ".875rem",
                FontWeight = "400",
                LineHeight = "1.45",
            },
            // Display cuts — heavier weight, negative tracking, tight leading
            H1 = new H1Typography { FontFamily = Display, FontSize = "2.5rem",   FontWeight = "700", LineHeight = "1.1",  LetterSpacing = "-.025em" },
            H2 = new H2Typography { FontFamily = Display, FontSize = "2.125rem", FontWeight = "700", LineHeight = "1.15", LetterSpacing = "-.022em" },
            H3 = new H3Typography { FontFamily = Display, FontSize = "1.75rem",  FontWeight = "700", LineHeight = "1.2",  LetterSpacing = "-.02em" },
            H4 = new H4Typography { FontFamily = Display, FontSize = "1.375rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "-.015em" },
            // Title cuts
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.3",  LetterSpacing = "-.01em" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "1rem",     FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-.005em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "1rem",      FontWeight = "600", LineHeight = "1.4" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = ".875rem",   FontWeight = "600", LineHeight = "1.4" },
            Body1     = new Body1Typography     { FontFamily = Sans, FontSize = ".875rem",   FontWeight = "400", LineHeight = "1.45" },
            Body2     = new Body2Typography     { FontFamily = Sans, FontSize = ".8125rem",  FontWeight = "400", LineHeight = "1.45" },
            Button    = new ButtonTypography
            {
                FontFamily    = Sans,
                FontSize      = ".875rem",
                FontWeight    = "600",
                LetterSpacing = ".01em",
                TextTransform = "none",   // sentence case — no Material shouting
            },
            Caption  = new CaptionTypography  { FontFamily = Sans, FontSize = ".75rem", FontWeight = "400", LineHeight = "1.4" },
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = ".75rem", FontWeight = "600", LetterSpacing = ".06em", TextTransform = "uppercase" },
        },
    };
}
