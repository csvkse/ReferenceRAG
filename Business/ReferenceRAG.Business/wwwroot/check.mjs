import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { compile } from './vendor/vue.js';

export const JS_GLOBALS = new Set([
  'true', 'false', 'null', 'undefined', 'NaN', 'Infinity',
  'Math', 'Date', 'Array', 'Object', 'String', 'Number', 'Boolean', 'RegExp', 'Error', 'JSON', 'console',
  'parseInt', 'parseFloat', 'isNaN', 'isFinite', 'encodeURI', 'encodeURIComponent', 'decodeURI', 'decodeURIComponent',
  '$event', '$slots', '$attrs', '$emit', '$refs', '$parent', '$root', 'window', 'document',
  'if', 'else', 'typeof', 'instanceof', 'void', 'delete', 'new', 'in', 'of', 'return',
  'case', 'break', 'continue', 'default', 'throw', 'catch', 'finally', 'try', 'switch',
  'while', 'do', 'for', 'const', 'let', 'var', 'function', 'async', 'await', 'yield', 'this'
]);

export function extractReturnedKeys(source) {
  const rMatch = source.match(/const\s+__returned__\s*=\s*\{([\s\S]*?)\};?\s*return\s+__returned__;/);
  const keys = new Set(JS_GLOBALS);
  if (!rMatch) return keys;
  const body = rMatch[1];
  for (const m of body.matchAll(/(?:get\s+([a-zA-Z0-9_$]+)\s*\(\)|([a-zA-Z0-9_$]+)(?:\s*:|\s*,|\s*$))/g)) {
    const k = m[1] || m[2];
    if (k && k !== 'get') keys.add(k);
  }
  return keys;
}

export function checkTemplateBindings(templateStr, returnedKeysSet) {
  const keysSet = returnedKeysSet instanceof Set ? returnedKeysSet : new Set(returnedKeysSet);
  for (const g of JS_GLOBALS) keysSet.add(g);

  // 1. Strip HTML comments
  const cleanHtml = templateStr.replace(/<!--[\s\S]*?-->/g, '');
  const scopedVars = new Set();

  // 2. Scoped vars from v-for: v-for="item in items" or v-for="(item, index) in items"
  for (const m of cleanHtml.matchAll(/v-for="([^"]+?)\s+(?:in|of)\s+[^"]+"/g)) {
    const lhs = m[1].replace(/[()]/g, '');
    for (const idMatch of lhs.matchAll(/\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b/g)) {
      scopedVars.add(idMatch[1]);
    }
  }

  // 3. Scoped vars from slots: e.g. #default="{ row }" or #header="slotProps"
  for (const m of cleanHtml.matchAll(/(?:#|v-slot:?)[a-zA-Z0-9_-]*="([^"]+)"/g)) {
    const slotBinding = m[1].replace(/[{}]/g, '');
    for (const idMatch of slotBinding.matchAll(/\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b/g)) {
      scopedVars.add(idMatch[1]);
    }
  }

  // 4. Extract expressions
  const exprs = [];
  for (const m of cleanHtml.matchAll(/\{\{([\s\S]*?)\}\}/g)) exprs.push(m[1]);
  for (const m of cleanHtml.matchAll(/(?:@[a-zA-Z0-9:.-]+|:[a-zA-Z0-9:.-]+|v-(?!for|slot)[a-zA-Z0-9:.-]+)="([^"]*)"/g)) {
    exprs.push(m[1]);
  }
  for (const m of cleanHtml.matchAll(/v-for="[^"]+?\s+(?:in|of)\s+([^"]+)"/g)) {
    exprs.push(m[1]);
  }

  const missing = new Set();
  for (const expr of exprs) {
    const cleanExpr = expr.replace(/'[^']*'/g, ' ').replace(/"[^"]*"/g, ' ').replace(/`[^`]*`/g, ' ');

    // Extract arrow function parameters: e.g. (row) => or v => or (a, b) => or () =>
    const localArrowParams = new Set();
    for (const arrowMatch of cleanExpr.matchAll(/(?:\(([^)]*)\)|([a-zA-Z_$][a-zA-Z0-9_$]*))\s*=>/g)) {
      const paramList = arrowMatch[1] ?? arrowMatch[2] ?? '';
      for (const p of paramList.matchAll(/\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b/g)) {
        localArrowParams.add(p[1]);
      }
    }

    for (const m of cleanExpr.matchAll(/\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b/g)) {
      const id = m[1];
      const idx = m.index;
      // Skip object property access: foo.bar or foo?.bar
      if (idx > 0) {
        if (cleanExpr[idx - 1] === '.') continue;
        if (idx >= 2 && cleanExpr.slice(idx - 2, idx) === '?.') continue;
      }
      // Skip object literal keys: { key: value }
      const after = cleanExpr.slice(idx + id.length).trimStart();
      if (after.startsWith(':') && !after.startsWith('::')) {
        const before = cleanExpr.slice(0, idx).trimEnd();
        if (before.endsWith('{') || before.endsWith(',')) continue;
      }
      if (!keysSet.has(id) && !scopedVars.has(id) && !localArrowParams.has(id)) {
        missing.add(id);
      }
    }
  }
  return Array.from(missing);
}

export function runQualityGate(rootDirectory) {
  let files = 0, templates = 0;
  const errors = [];

  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (['vendor', 'tests', 'node_modules'].includes(entry.name)) continue;
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) { visit(full); continue; }
      if (!entry.name.endsWith('.js')) continue;

      const relPath = path.relative(rootDirectory, full).replace(/\\/g, '/');

      // 1. Syntax check via node --check
      const result = spawnSync(process.execPath, ['--check', full], { encoding: 'utf8' });
      if (result.status !== 0) {
        errors.push(`Syntax error in ${relPath}: ${result.stderr}`);
        continue;
      }

      const source = fs.readFileSync(full, 'utf8');
      files++;

      // 2. Local dependency check
      for (const match of source.matchAll(/(?:from\s*|import\s*\()(['"])(\/[^'"]+)\1/g)) {
        if (!fs.existsSync(path.join(rootDirectory, match[2]))) {
          errors.push(`Missing local dependency in ${relPath}: ${match[2]}`);
        }
      }

      // 3. Vue template validation & unbound identifier check
      const templateMatch = source.match(/^component\.template = (".*");$/m);
      if (templateMatch) {
        try {
          const rawTemplate = JSON.parse(templateMatch[1]);
          compile(rawTemplate, { decodeEntities: value => value });
          templates++;

          const returnedKeys = extractReturnedKeys(source);
          const missing = checkTemplateBindings(rawTemplate, returnedKeys);

          if (missing.length > 0) {
            errors.push(`Unbound template identifiers in ${relPath}: [${missing.join(', ')}] not returned by setup()`);
          }
        } catch (err) {
          errors.push(`Template compile error in ${relPath}: ${err.message}`);
        }
      }
    }
  }

  visit(rootDirectory);
  return { files, templates, errors };
}

const currentFile = fileURLToPath(import.meta.url);
const root = path.dirname(currentFile);

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(currentFile)) {
  const { files, templates, errors } = runQualityGate(root);
  if (errors.length > 0) {
    console.error(`\x1b[31m[Quality Gate Failed] Found ${errors.length} frontend issue(s):\x1b[0m`);
    for (const err of errors) console.error(`  - ${err}`);
    process.exit(1);
  }
  console.log(`Validated ${files} JS modules and ${templates} executable Vue templates (AST symbol bindings verified); no build output generated.`);
}
