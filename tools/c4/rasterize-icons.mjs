// Draws every icon under tools/c4/icons as a 96 px PNG in tools/c4/icons/png, the fallback a deck shows where a
// viewer cannot draw the SVG it carries beside it. Line icons are drawn in slate; Azure's icons as they are.
//
//   node tools/c4/rasterize-icons.mjs
//
// It uses the Playwright that the GUI installs (osdu/gui: npm ci), with its own Chromium, or Chrome when that is absent.

import { createRequire } from 'node:module';
import { mkdir, readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const icons = path.join(here, 'icons');
const out = path.join(icons, 'png');
const require = createRequire(process.env.C4_GUI_PACKAGE ?? path.join(here, '..', '..', 'osdu', 'gui', 'package.json'));
const { chromium } = require('playwright-core');

const SIZE = 96;
const SLATE = '#334155';

async function launch() {
  try {
    return await chromium.launch();
  } catch (bundled) {
    try {
      return await chromium.launch({ channel: 'chrome' });
    } catch (chrome) {
      throw new Error(`no browser to draw the icons with: ${bundled.message.split('\n')[0]}; ${chrome.message.split('\n')[0]}`);
    }
  }
}

await mkdir(out, { recursive: true });
const browser = await launch();
try {
  const page = await browser.newPage({ viewport: { width: SIZE, height: SIZE }, deviceScaleFactor: 1 });
  let count = 0;
  for (const family of ['lucide', 'azure']) {
    for (const file of (await readdir(path.join(icons, family))).filter((f) => f.endsWith('.svg')).sort()) {
      let svg = await readFile(path.join(icons, family, file), 'utf8');
      if (family === 'lucide') svg = svg.replaceAll('currentColor', SLATE);
      svg = svg.replace(/<svg([^>]*?)\swidth="[^"]*"/, '<svg$1').replace(/<svg([^>]*?)\sheight="[^"]*"/, '<svg$1');
      svg = svg.replace('<svg', `<svg width="${SIZE}" height="${SIZE}"`);
      await page.setContent(`<html><body style="margin:0;background:transparent">${svg}</body></html>`);
      await page.locator('svg').first().screenshot({
        path: path.join(out, `${family}-${file.replace(/\.svg$/, '.png')}`),
        omitBackground: true,
      });
      count += 1;
    }
  }
  console.log(`drew ${count} icons into ${path.relative(process.cwd(), out) || out}`);
} finally {
  await browser.close();
}
