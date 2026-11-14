import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const wwwRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const html = fs.readFileSync(path.join(wwwRoot, 'index.html'), 'utf8');
const match = html.match(/<script type="importmap">\s*([\s\S]*?)<\/script>/);
const imports = match ? JSON.parse(match[1]).imports : {};

export function resolve(specifier, context, nextResolve) {
  const mapped = imports[specifier];
  if (mapped && mapped.startsWith('/')) {
    const file = path.join(wwwRoot, mapped.split('?')[0]);
    return { url: pathToFileURL(file).href, shortCircuit: true };
  }
  return nextResolve(specifier, context);
}