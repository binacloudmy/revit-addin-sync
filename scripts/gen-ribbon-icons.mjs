// Renders the Bina ribbon icon set (design canvas "Bina Ribbon Icons") to the
// SVG sources + PNG rasters the ribbon embeds.
//
// Two drawings per icon, not one scaled: the 32 grid is stroked at 1.8px and
// the 16 grid is redrawn at 1.3px with detail removed, exactly as the design
// specifies. 64 is the 32 drawing rendered at 2x for high-DPI displays.
//
//   node scripts/gen-ribbon-icons.mjs Resources/Icons
import { Resvg } from '@resvg/resvg-js';
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const OUT = process.argv[2];
const SVG_DIR = join(OUT, 'svg');
mkdirSync(SVG_DIR, { recursive: true });

// Graphite. All structure — outlines, containers, arrows.
const INK = '#33383D';

const ICONS = {
  LoginCde: {
    32: `<path d="M9.8 21.6h12.9a4 4 0 000-8 5.6 5.6 0 00-10.7-1.8 3.9 3.9 0 00-2.2 9.8z" fill="#C4DFF3" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <path d="M12.4 16.8h6.8" fill="none" stroke="currentColor" stroke-width="1.8"/>
         <path d="M16.8 14.4l2.4 2.4-2.4 2.4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>`,
    16: `<path d="M4.9 11h6.5a2.1 2.1 0 000-4.2 2.9 2.9 0 00-5.6-.9 2 2 0 00-.9 5.1z" fill="#C4DFF3" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <path d="M6.4 8.6h3.2" fill="none" stroke="currentColor" stroke-width="1.3"/>
         <path d="M8.4 7.4l1.2 1.2-1.2 1.2" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>`
  },
  Sync: {
    32: `<g fill="none" stroke="#3E8FCB" stroke-width="1.8" stroke-linejoin="round">
           <path d="M6.8 17.8A9.2 9.2 0 0 1 21 8.2"/>
           <path d="M16.2 6l5.2 1.5-1.5 5.2"/>
           <g transform="rotate(180 16 16)">
             <path d="M6.8 17.8A9.2 9.2 0 0 1 21 8.2"/>
             <path d="M16.2 6l5.2 1.5-1.5 5.2"/>
           </g>
         </g>`,
    16: `<g fill="none" stroke="#3E8FCB" stroke-width="1.3" stroke-linejoin="round">
           <path d="M3.4 8.9A4.6 4.6 0 0 1 10.5 4.1"/>
           <path d="M8.1 3l2.6.8-.8 2.6"/>
           <g transform="rotate(180 8 8)">
             <path d="M3.4 8.9A4.6 4.6 0 0 1 10.5 4.1"/>
             <path d="M8.1 3l2.6.8-.8 2.6"/>
           </g>
         </g>`
  },
  SyncParameters: {
    32: `<g fill="none" stroke="currentColor" stroke-width="1.8">
           <path d="M5 11.5h5.4"/><path d="M16.6 11.5H27"/>
           <path d="M5 20.5h11.4"/><path d="M22.6 20.5H27"/>
           <circle cx="13.5" cy="11.5" r="3.1" fill="#4E9AD3"/>
           <circle cx="19.5" cy="20.5" r="3.1" fill="#4E9AD3"/>
         </g>`,
    16: `<g fill="none" stroke="currentColor" stroke-width="1.3">
           <path d="M2.4 5.6h2.3"/><path d="M8.1 5.6h5.5"/>
           <path d="M2.4 10.4h5.3"/><path d="M11.3 10.4h2.3"/>
           <circle cx="6.4" cy="5.6" r="1.7" fill="#4E9AD3"/>
           <circle cx="9.6" cy="10.4" r="1.7" fill="#4E9AD3"/>
         </g>`
  },
  Issues: {
    32: `<path d="M6.6 10.4a2 2 0 012-2h14.8a2 2 0 012 2v8.4a2 2 0 01-2 2h-6.6L11.4 25v-4.2H8.6a2 2 0 01-2-2z" fill="#C4DFF3" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <path d="M16 11.4v4.2" fill="none" stroke="#E0761A" stroke-width="2.2" stroke-linecap="round"/>
         <circle cx="16" cy="17.9" r="1.2" fill="#E0761A"/>`,
    16: `<path d="M2.4 4.9a1.3 1.3 0 011.3-1.3h8.6a1.3 1.3 0 011.3 1.3v4.4a1.3 1.3 0 01-1.3 1.3H8.6L5.6 12.9v-2.3H3.7a1.3 1.3 0 01-1.3-1.3z" fill="#C4DFF3" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <path d="M8 5.4v2.2" fill="none" stroke="#E0761A" stroke-width="1.6" stroke-linecap="round"/>
         <circle cx="8" cy="8.9" r="0.8" fill="#E0761A"/>`
  },
  DownloadModel: {
    32: `<g fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round">
           <path d="M13.4 4.6l8.8 4.9-8.8 4.9-8.8-4.9z" fill="#C4DFF3"/>
           <path d="M13.4 4.6l8.8 4.9v9.8l-8.8 4.9-8.8-4.9V9.5z"/>
           <path d="M4.6 9.5l8.8 4.9 8.8-4.9"/>
           <path d="M13.4 14.4v9.8"/>
           <g stroke="#3E8FCB" stroke-width="2"><path d="M25.8 18.2v8.4"/><path d="M23 23.8l2.8 2.8 2.8-2.8"/></g>
         </g>`,
    16: `<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round">
           <path d="M6.4 2.4l4.4 2.4-4.4 2.4L2 4.8z" fill="#C4DFF3"/>
           <path d="M6.4 2.4l4.4 2.4v4.9l-4.4 2.4L2 9.7V4.8z"/>
           <path d="M2 4.8l4.4 2.4 4.4-2.4"/>
           <path d="M6.4 7.2v4.9"/>
           <g stroke="#3E8FCB" stroke-width="1.5"><path d="M12.9 9.2v4.3"/><path d="M11.5 12.1l1.4 1.4 1.4-1.4"/></g>
         </g>`
  },
  LoginAi: {
    32: `<path d="M13.4 7.4h9.4a2.2 2.2 0 012.2 2.2v12.8a2.2 2.2 0 01-2.2 2.2h-9.4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round">
           <path d="M4.6 16h8.4"/>
           <path d="M10.6 13.2L13.4 16l-2.8 2.8"/>
         </g>
         <path d="M19.6 12.4l1.2 3 3 1.2-3 1.2-1.2 3-1.2-3-3-1.2 3-1.2z" fill="#D93B94"/>`,
    16: `<path d="M6.8 3.6h4.4a1.4 1.4 0 011.4 1.4v6a1.4 1.4 0 01-1.4 1.4H6.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round">
           <path d="M2 8h4.6"/>
           <path d="M5.2 6.6L6.6 8 5.2 9.4"/>
         </g>
         <path d="M9.9 5.9l.7 1.7 1.7.7-1.7.7-.7 1.7-.7-1.7-1.7-.7 1.7-.7z" fill="#D93B94"/>`
  },
  AiAssistant: {
    32: `<path d="M13.4 4.8l2.5 6.4 6.4 2.5-6.4 2.5-2.5 6.4-2.5-6.4-6.4-2.5 6.4-2.5z" fill="#D93B94"/>
         <path d="M22.8 18.6l1.1 2.9 2.9 1.1-2.9 1.1-1.1 2.9-1.1-2.9-2.9-1.1 2.9-1.1z" fill="#D93B94"/>`,
    16: `<path d="M6.6 2.2l1.4 3.5 3.5 1.4-3.5 1.4-1.4 3.5-1.4-3.5-3.5-1.4 3.5-1.4z" fill="#D93B94"/>
         <path d="M11.7 9.8l.6 1.6 1.6.6-1.6.6-.6 1.6-.6-1.6-1.6-.6 1.6-.6z" fill="#D93B94"/>`
  },
  JkrCompliance: {
    32: `<path d="M12.6 6H9.8a2.2 2.2 0 00-2.2 2.2v15.6a2.2 2.2 0 002.2 2.2h12.4a2.2 2.2 0 002.2-2.2V8.2A2.2 2.2 0 0022.2 6h-2.8" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <rect x="12.6" y="4.2" width="6.8" height="3.6" rx="0.9" fill="none" stroke="currentColor" stroke-width="1.8"/>
         <path d="M11.8 16.6l3.2 3.2 5.6-6.4" fill="none" stroke="#3F9E52" stroke-width="2.4" stroke-linejoin="round"/>`,
    16: `<path d="M6.2 2.9H4.6a1.2 1.2 0 00-1.2 1.2v8.4a1.2 1.2 0 001.2 1.2h6.8a1.2 1.2 0 001.2-1.2V4.1a1.2 1.2 0 00-1.2-1.2H9.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <rect x="6.2" y="2" width="3.6" height="1.9" rx="0.5" fill="none" stroke="currentColor" stroke-width="1.3"/>
         <path d="M5.8 8.3l1.7 1.7 3-3.4" fill="none" stroke="#3F9E52" stroke-width="1.7" stroke-linejoin="round"/>`
  },
  BombaCompliance: {
    32: `<path d="M12.6 6H9.8a2.2 2.2 0 00-2.2 2.2v15.6a2.2 2.2 0 002.2 2.2h12.4a2.2 2.2 0 002.2-2.2V8.2A2.2 2.2 0 0022.2 6h-2.8" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <rect x="12.6" y="4.2" width="6.8" height="3.6" rx="0.9" fill="none" stroke="currentColor" stroke-width="1.8"/>
         <path d="M10.7 21.2a5.3 5.3 0 0010.6 0c0-1.7-.7-3.3-1.8-4.7l-1.2 2-1.7-7-2.3 4.7-1.2-1.5c-1.5 1.7-2.4 3.7-2.4 6.5z" fill="#F2A03C" stroke="#D26610" stroke-width="1.6" stroke-linejoin="round"/>`,
    16: `<path d="M6.2 2.9H4.6a1.2 1.2 0 00-1.2 1.2v8.4a1.2 1.2 0 001.2 1.2h6.8a1.2 1.2 0 001.2-1.2V4.1a1.2 1.2 0 00-1.2-1.2H9.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <rect x="6.2" y="2" width="3.6" height="1.9" rx="0.5" fill="none" stroke="currentColor" stroke-width="1.3"/>
         <path d="M5.6 11a2.9 2.9 0 005.8 0c0-1-.4-1.9-1-2.7l-.7 1.2-.9-4-1.3 2.7-.7-.9c-.8 1-1.2 2.1-1.2 3.7z" fill="#F2A03C" stroke="#D26610" stroke-width="1.2" stroke-linejoin="round"/>`
  },
  CostTracker: {
    32: `<path d="M5.4 5v21.6H27" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.8">
           <rect x="9.4" y="18.4" width="4.4" height="8.2" fill="#C4DFF3"/>
           <rect x="15.6" y="14.4" width="4.4" height="12.2" fill="#7FBEE4"/>
           <rect x="21.8" y="9.4" width="4.4" height="17.2" fill="#3E8FCB"/>
         </g>`,
    16: `<path d="M2.7 2.5v10.8h10.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.3">
           <rect x="4.9" y="9.4" width="2.4" height="3.9" fill="#C4DFF3"/>
           <rect x="8.4" y="6.7" width="2.4" height="6.6" fill="#3E8FCB"/>
         </g>`
  }
};

const svgFor = (body, grid) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${grid} ${grid}" width="${grid}" height="${grid}" fill="none">\n` +
  body.replace(/currentColor/g, INK).trim() + `\n</svg>\n`;

let n = 0;
for (const [name, grids] of Object.entries(ICONS)) {
  for (const grid of [32, 16]) {
    const svg = svgFor(grids[grid], grid);
    writeFileSync(join(SVG_DIR, `${name}${grid}.svg`), svg);
    // 32 also renders at 2x for the high-DPI ribbon.
    for (const px of grid === 32 ? [32, 64] : [16]) {
      const png = new Resvg(svg, { fitTo: { mode: 'width', value: px } }).render().asPng();
      writeFileSync(join(OUT, `${name}${px}.png`), png);
      n++;
    }
  }
}
console.log(`wrote ${n} PNGs + ${Object.keys(ICONS).length * 2} SVGs to ${OUT}`);
