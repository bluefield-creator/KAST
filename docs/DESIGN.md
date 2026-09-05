# Artoria Design System — Blue Kepi Edition

CASTER's design language, rebuilt from the visual-forensics dossier of
Artoria Caster (2nd Ascension): four plates, three hands, region-mode
pixel samples. The UI is not themed *after* her — it is structured *as*
her. One permanent theme: inside the coat at night.

The soul, quoted from the dossier's design philosophy:

> Push every expressive element outboard onto free-moving material, keep
> the body small and quiet, and let a rigid, correct, borrowed uniform
> sit on a soft, round, uncertain face.

Translated: **the content is the body — small, quiet, dense. All
expression lives at the edges** — the app bar, the drawer, the footer,
the openings. The middle of every page stays calm.

Tokens: `src/KAST.UI/wwwroot/css/artoria-tokens.css` · MudBlazor layer:
`wwwroot/css/artoria.css` · C# palette: `Themes/ArtoriaTheme.cs`.

## The laws (zero violations in the source; zero violations here)

1. **Crimson is lining.** `#C8203C` never rests on an outer surface. It
   appears only when something opens or is acted upon: the band inside a
   dialog's top edge, the line revealed under a pressed or hovered
   primary button, the underline of a crashed status. Emotion is what
   the uniform reveals, not what it displays.
2. **Brass outlines every cool edge.** The four-value metal ladder —
   hardware `#8A5F2E`/`#C89A5E`, trim `#EBCB95`/`#F0DCB4` — mediates
   every cool/warm boundary: focus rings, active rails, title rules,
   piping. Gold is never a fill.
3. **The ladder motif recurs at every scale.** Paired verticals crossed
   by rungs: the drawer's double right edge, the nav's button column,
   the belt-header's twin pinstripes, the kepi band's twin gold lines —
   the same grammar from app-shell scale down to a 4px tab slider.
4. **Circles are repetition; two may be singular.** Buttons repeat as
   small domed fittings (hemisphere with a crescent highlight, never a
   flat disc). At most two singular circles per view — the avatar and
   one focal control — everything else squares off (radii 2–4px).
5. **Shadows lean violet, never grey.** Whites fold through `#E4E4E4`
   into `#C3B0BE`; muted text is violet-mist, never `#888`.
6. **Aubergine is the bridge.** `#5A3A5C` sits between indigo and
   crimson in hue — so it is the *hover* family: the state between rest
   and reveal.
7. **The darkest values sit on the moving parts.** Consoles and progress
   tracks wear glove-charcoal `#303030` and staff-black `#35343F` — the
   terminals, where the hands are.
8. **The face is ringed, not spotlit.** Focal content wins by being the
   calm center of an ornamented ring, never by being the loudest thing.
9. **Sheen lives in lettering only.** The blonde gradient
   (`#FFF3D9 → #F5D8A8 → #FBC9A0 → #DDA389`, crown-gold into apricot) is
   clipped to display type: the wordmark, dialog titles. Her hair is the
   only thing that shines.
10. **One color is unissued: the ribbon.** The user accent defaults to
    lilac `#DDA8D6` and is chosen from ribbon-grade options (Cabochon,
    Eye Light, Cream Braid, Apricot, Lining Blush). It is the one thing
    the operator ties on — everything else is regulation.

## Anatomy — which part of her each region is

| UI region | Her anatomy | Treatment |
|---|---|---|
| App bar | **The kepi** | Flat `#0E0E24` crown; base wears the crimson band between two gold pinstripes |
| Wordmark | **Her hair** | Serif, blonde-gradient sheen, one brass staff-light `✦` |
| Nav drawer | **The frogged capelet** | Column of domed button fittings; the active item's button turns brass, tied by its braid-bar rail |
| Active tab | **The kepi band** (2nd scale) | 4px crimson slider hairlined in cream above and below |
| Table headers | **The belt** | Navy `#1C1C4A` with twin gold pinstripes |
| Primary button | **The tunic** | White, ink-outlined; hover reveals the crimson lining along the inner bottom edge |
| Dialogs | **The coat, opening** | Double gold piping (cream band + inner hairline), crimson band revealed inside the top edge |
| Hovers/selection | **The hosiery** | Aubergine washes — the bridge hue |
| Consoles, progress tracks | **Gloves & staff** | Charcoal/staff-black terminals; progress fill is the brass collar |
| Running status | **Her eyes** | `#8FC3A8` — the only saturated green anywhere; the true focal color |
| Info/links | **The cabochon** | Stone blue `#91BDE0`, cool and luminous, used sparingly |
| Warnings | **Hair-tip apricot** | `#DDA389` — warmth, deliberately not gold |
| User accent | **The staff ribbon** | `#DDA8D6` default; the one unissued color |

## Palette (sampled)

Uniform indigo `#24225E / #3B3A86 / #5A57A8` (violet-shifted — never
navy), UI registers derived downward to canvas `#131331`, plate
`#191940`, belt `#1C1C4A`, hairline `#34336E`, ink `#101022`. Tunic
`#FFFFFF / #E4E4E4 / #C3B0BE`. Crimson `#7C0F1B / #C8203C / #DD4F4D`.
Brass ladder `#8A5F2E / #C89A5E / #EBCB95 / #F0DCB4`. Blonde
`#FFF3D9 → #DDA389`. Aubergine `#3A2440 / #5A3A5C / #7A5A7C`. Eye
`#374F37 / #4E8C7E / #D5ECB8` · stone `#91BDE0` · ribbon `#DDA8D6` ·
charcoal `#303030 / #545454` · staff `#35343F` · mists `#AAA6C9 / #71738D`.

Only three things are allowed to be saturated at once: the crimson, the
brass, and the eye-green. Everything else stays near-neutral or
desaturated — that restraint is what makes them land.

## Typography & shape

One face, two optical cuts, in the Apple manner: **Inter** for display
and body alike (display sizes go heavier and tighten to −.015…−.025em
rather than switching to a serif), **JetBrains Mono** for anything that is
data — the war log, tallies, keys. Inter runs with `cv11 ss01 tnum` so
digits stay tabular and the `a` stays single-storey. The Cinzel wordmark
survives only inside the SVG logo. Buttons sentence-case, 600, `.01em`.
Navigation is a dense, edge-to-edge grouped list (32px rows, 28px
sub-rows, small-caps group labels) — the brass rail marks the active row.
Radii 2–4px: stiff, tailored, squared away. Motion 120–240ms ease-out —
the rigid pieces do not deform; only the outboard material moves.

## Forbidden

Crimson as a resting fill · gold fills or gold body text · glows, blur,
gradients on surfaces (gradients exist only clipped to lettering and in
her hair) · neutral grey shadows · pill shapes and radii above 8px ·
more than two singular circles per view · saturating anything beyond
the crimson/brass/eye-green trio · spotlighting focal content instead of
ringing it.
