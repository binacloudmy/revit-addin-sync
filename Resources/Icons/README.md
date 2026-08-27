# Bina ribbon icons

Source of truth is the design canvas **Bina Ribbon Icons** (claude.ai/design,
project `ce01ca04-e3f4-4d37-93ab-bcf107ef0164`). The masters here are the
`svg/` files; the PNGs are generated.

## The rules the set follows

- **Structure in graphite `#33383D`**, one flat accent colour on the part that
  carries the meaning. Never two accents in one icon — that is what Autodesk's
  own set does, and it is why these read as Revit icons rather than as a
  third-party add-in.
- **Weight** 1.8px on the 32 grid, 1.3px on the 16.
- **No tiles.** Nothing sits on a coloured plate; Revit's ribbon is a light
  grey field and its icons float directly on it.
- **Grouping is carried by the panel labels**, not by colour.
- **The 16 is redrawn, not scaled.** Cost Tracker drops a bar, the Bomba flame
  loses a tongue. Fills stay — at 16px colour does more work than line.

Accents: `#3E8FCB` blue (data and model surfaces), `#D93B94` magenta (the AI
group), `#3F9E52` green (a pass — only the JKR tick), `#F2A03C`/`#E0761A` amber
to orange (attention and fire).

## Sizes

`<Name>16.png` — Revit's `PushButtonData.Image`, drawn on the 16 grid.
`<Name>32.png` — the 32 drawing at 1x. Not currently bound; kept because it is
the nominal ribbon size and anything outside `App.cs` will want it.
`<Name>64.png` — the 32 drawing at 2x, for high-DPI. Embedded but **not
bound**: AdWindows does not constrain the large slot in every button template,
so handing `LargeImage` an oversized `BitmapImage` can render it at its natural
size and blow the button out. Wiring it needs a DPI check verified on a real
Revit — see "Not done yet".

## Regenerating

```
node scripts/gen-ribbon-icons.mjs Resources/Icons
```

Needs `@resvg/resvg-js` (`npm i @resvg/resvg-js`). Edit the paths in that
script — it holds the drawings — not the PNGs.

## Not done yet

**Dark theme.** The accents hold up on Revit's dark theme but the graphite
structure does not; a dark build needs the line work lifted to a light grey
with the same fills, and a second set of PNGs.

**High-DPI.** The 64s are sitting there unused. Serving them to `LargeImage`
above ~150% Windows scaling is the obvious win, but it has to be tried in
Revit first — see the size note above. Nothing here has been run in Revit;
it was authored and rendered on macOS.
