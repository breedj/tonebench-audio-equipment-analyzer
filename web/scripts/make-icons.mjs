// Renders packaging/icons/tonebench.svg into every raster the packages need. Run with `npm run icons`
// from web/ (it lives here so ESM resolution finds web/node_modules); the outputs are committed, so
// this only needs running again when the SVG changes.
//
//   packaging/windows/tonebench.ico                 16..256 px, the exe's icon and the installer's
//   packaging/linux/tonebench.png    256 px, hicolor/256x256 in the .deb
//   packaging/linux/tonebench.svg    copy, hicolor/scalable in the .deb
//   web/public/favicon.svg                      copy, so the browser tab matches the shortcut
import { copyFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import pngToIco from 'png-to-ico';
import sharp from 'sharp';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const source = resolve(root, 'packaging/icons/tonebench.svg');

async function raster(size) {
  return sharp(source, { density: 384 }).resize(size, size).png().toBuffer();
}

const icoSizes = [16, 24, 32, 48, 64, 128, 256];
const icoPngs = await Promise.all(icoSizes.map(raster));
await mkdir(resolve(root, 'packaging/windows'), { recursive: true });
await writeFile(resolve(root, 'packaging/windows/tonebench.ico'), await pngToIco(icoPngs));

await mkdir(resolve(root, 'packaging/linux'), { recursive: true });
await writeFile(resolve(root, 'packaging/linux/tonebench.png'), await raster(256));
await copyFile(source, resolve(root, 'packaging/linux/tonebench.svg'));
await copyFile(source, resolve(root, 'web/public/favicon.svg'));

console.log(`icons written from ${source}`);
