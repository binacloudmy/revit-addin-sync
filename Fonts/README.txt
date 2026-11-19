Fonts for the copilot-reasoning UI (2026-08-02 spec)
=====================================================

This folder is a PLACEHOLDER. It exists so the csproj's wildcard Resource
globs (`Fonts\*.ttf`, `Fonts\*.otf`) and the pack URIs in
UI/Copilot/CopilotTokens.xaml (Cp.Reasoning.Font / Cp.Reasoning.FontMono)
resolve to something real the moment the actual font files are dropped in —
no further code change needed ("just works").

Drop these files here (exact names don't matter — the wildcard picks up any
.ttf/.otf — but the FONT FAMILY NAME embedded in each file must match what
the tokens reference):

  Instrument Sans (UI text) — https://fonts.google.com/specimen/Instrument+Sans
    - InstrumentSans-Regular.ttf   (400)
    - InstrumentSans-Medium.ttf    (500)
    - InstrumentSans-SemiBold.ttf  (600)

  JetBrains Mono (metadata/counts/timers/mono labels) —
  https://www.jetbrains.com/lp/mono/
    - JetBrainsMono-Regular.ttf    (400)
    - JetBrainsMono-Medium.ttf     (500)

Both are open-source / free for commercial embedding (OFL-1.1) — check the
license file bundled with each download before shipping, same diligence as
any other embedded asset.

Until real files land here, every {DynamicResource Cp.Reasoning.Font} /
Cp.Reasoning.FontMono reference in the reasoning-UI screens (reasoning
timeline, approval card, result card, composer) falls through to the OS
fallback names baked into the same token ("Segoe UI, system-ui, sans-serif"
/ "Cascadia Mono, Consolas, Courier New, monospace") — same graceful-
degradation pattern the pane already uses for Cp.Font/Cp.FontMono (see the
"Task 17" comment in CopilotTokens.xaml). Nothing breaks either way; the
reasoning-UI screens just render in Segoe UI/Consolas instead of Instrument
Sans/JetBrains Mono until this folder has real content.

WINDOWS BUILDER CHECKLIST when the real files are available:
  1. Copy the .ttf files into this folder.
  2. Build once (net8.0-windows is enough for a quick check) and open the
     Copilot pane — the reasoning timeline's mono step labels and the
     composer hint row are the fastest visual tell (JetBrains Mono has a
     distinctly boxier "1" and slashed "0" vs Consolas).
  3. If the family doesn't apply, open each .ttf's own font-name metadata
     (right-click > Properties on Windows, or `fc-scan` on macOS/Linux) and
     confirm it matches "Instrument Sans" / "JetBrains Mono" EXACTLY (the
     string after the # in the pack URI) — variable-font builds sometimes
     report a different family name (e.g. "Instrument Sans Variable") than
     the static weights this token assumes.


JKR Compliance pane (Clear Sheet, 2026-11-19)
---------------------------------------------
Public Sans 400/500/600/700 (UI) and IBM Plex Mono 400/500 (values, ids) are bundled
for Bz.Font.Sans / Bz.Font.Mono in UI/Jkr/Panel/PanelTokens.xaml (OFL-1.1; licences in
PublicSans-LICENSE.txt, IBMPlexMono-LICENSE.txt). Public Sans is static instances cut from
the Google Fonts variable TTF (fontTools instancer), since WPF ignores variable axes.
The Medium / SemiBold instances are renamed so nameID1 = the family ("Public Sans", "IBM Plex Mono") and nameID2 = the style, because WPF keys on the legacy family name and otherwise FontWeight 500/600 falls back to Regular/Bold. Tests/ClearSheetFontResolutionTests.cs guards this.
