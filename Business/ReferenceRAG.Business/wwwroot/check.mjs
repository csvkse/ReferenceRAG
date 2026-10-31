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

export function checkReturnedScope(source) {
  const rMatch = source.match(/const\s+__returned__\s*=\s*\{([\s\S]*?)\};?\s*return\s+__returned__;/);
  if (!rMatch) return [];

  const body = rMatch[1];
  const returnedKeys = [];
  for (const m of body.matchAll(/(?:get\s+([a-zA-Z0-9_$]+)\s*\(\)|([a-zA-Z0-9_$]+)(?:\s*:|\s*,|\s*$))/g)) {
    const k = m[1] || m[2];
    if (k && k !== 'get' && k !== 'set') returnedKeys.push(k);
  }

  const setupParamMatch = source.match(/setup\s*\(([^)]*)\)/);
  const setupParams = new Set();
  if (setupParamMatch) {
    for (const pm of setupParamMatch[1].matchAll(/\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b/g)) {
      setupParams.add(pm[1]);
    }
  }

  const beforeSource = source.substring(0, rMatch.index);
  const undefinedKeys = [];
  for (const key of returnedKeys) {
    if (JS_GLOBALS.has(key) || setupParams.has(key)) continue;
    const declRegex = new RegExp(`(?:const|let|var)\\s+[^;=]*?\\b${key}\\b|(?:function|class)\\s+${key}\\b|import\\s+[^;]*?\\b${key}\\b`);
    if (!declRegex.test(beforeSource)) {
      undefinedKeys.push(key);
    }
  }
  return undefinedKeys;
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

/**
 * 解析 index.html 中的 <script type="importmap"> 映射表
 */
export function extractImportMap(rootDirectory) {
  const htmlPath = path.join(rootDirectory, 'index.html');
  if (!fs.existsSync(htmlPath)) return {};
  const html = fs.readFileSync(htmlPath, 'utf8');
  const mapMatch = html.match(/<script type="importmap">([\s\S]*?)<\/script>/);
  if (!mapMatch) return {};
  try {
    const parsed = JSON.parse(mapMatch[1]);
    return parsed.imports || {};
  } catch {
    return {};
  }
}

/**
 * 依据 importmap、根目录或相对路径解析模块规范名对应的绝对物理路径
 */
export function resolveModuleSpecifier(rootDirectory, fromFile, specifier, importMap = {}) {
  if (specifier in importMap) {
    const mapped = importMap[specifier];
    if (mapped.startsWith('/')) {
      return path.resolve(rootDirectory, mapped.slice(1));
    }
    return path.resolve(rootDirectory, mapped);
  }
  if (specifier.startsWith('/')) {
    return path.resolve(rootDirectory, specifier.slice(1));
  }
  if (specifier.startsWith('.')) {
    return path.resolve(path.dirname(fromFile), specifier);
  }
  return null;
}

/**
 * 静态解析目标 ES 模块中导出的全部符号集合（含 default、具名导出、重导出、通配符 re-export）
 */
export function getModuleExports(filePath, rootDirectory, cache = new Map(), visited = new Set()) {
  const normPath = path.resolve(filePath);
  if (cache.has(normPath)) return cache.get(normPath);
  if (visited.has(normPath)) return new Set();
  visited.add(normPath);

  if (!fs.existsSync(normPath)) return null;
  const source = fs.readFileSync(normPath, 'utf8');
  const exports = new Set();

  // 1. export default ...
  if (/export\s+default\b/.test(source)) {
    exports.add('default');
  }

  // 2. export (async) function / class
  for (const m of source.matchAll(/export\s+(?:async\s+)?(?:function|class)\s+([a-zA-Z0-9_$]+)/g)) {
    exports.add(m[1]);
  }

  // 3. export (const|let|var) a = ..., b = ...
  for (const m of source.matchAll(/export\s+(?:const|let|var)\s+([^;=]+?)(?:;|=)/g)) {
    const decls = m[1];
    for (const dm of decls.matchAll(/\b([a-zA-Z0-9_$]+)\b/g)) {
      exports.add(dm[1]);
    }
  }

  // 4. export { a, b as c, default as d } [from '...']
  for (const m of source.matchAll(/export\s*\{([\s\S]*?)\}(?:\s*from\s*['"]([^'"]+)['"])?/g)) {
    const list = m[1];
    for (const item of list.split(',')) {
      const trimmed = item.trim();
      if (!trimmed) continue;
      const asMatch = trimmed.match(/(?:^|\b)(?:[a-zA-Z0-9_$]+\s+as\s+)?([a-zA-Z0-9_$]+)$/);
      if (asMatch) {
        exports.add(asMatch[1]);
      }
    }
  }

  // 5. export * from '...'
  for (const m of source.matchAll(/export\s*\*\s*from\s*['"]([^'"]+)['"]/g)) {
    const reexportSpecifier = m[1];
    let reexportPath;
    if (reexportSpecifier.startsWith('.')) {
      reexportPath = path.resolve(path.dirname(normPath), reexportSpecifier);
    } else if (reexportSpecifier.startsWith('/')) {
      reexportPath = path.resolve(rootDirectory, reexportSpecifier.slice(1));
    }
    if (reexportPath) {
      const nested = getModuleExports(reexportPath, rootDirectory, cache, visited);
      if (nested) {
        for (const ne of nested) exports.add(ne);
      }
    }
  }

  cache.set(normPath, exports);
  return exports;
}

/**
 * 校验指定 JS 模块的所有 ESM import 语句，确认目标模块存在且导出了所导入的符号
 */
export function checkModuleImports(rootDirectory, fullPath, source, importMap = {}, cache = new Map()) {
  const issues = [];
  const relPath = path.relative(rootDirectory, fullPath).replace(/\\/g, '/');

  // Match ES module import statements:
  // e.g. import defaultItem from '...'
  //      import { a, b as c } from '...'
  //      import defaultItem, { a } from '...'
  //      import * as ns from '...'
  //      import '...'
  const importRegex = /import\s+(?:([\w$]+)\s*,?\s*)?(?:(?:\*\s+as\s+([\w$]+))|(?=\{)|(\{[\s\S]*?\}))?\s*from\s*['"]([^'"]+)['"]/g;

  for (const match of source.matchAll(importRegex)) {
    const defaultImport = match[1];
    const namespaceImport = match[2];
    const namedImportsBlock = match[3];
    const specifier = match[4];

    const resolvedPath = resolveModuleSpecifier(rootDirectory, fullPath, specifier, importMap);
    if (!resolvedPath) {
      issues.push(`Unresolved module specifier '${specifier}' in ${relPath}`);
      continue;
    }

    if (!fs.existsSync(resolvedPath)) {
      const relResolved = path.relative(rootDirectory, resolvedPath).replace(/\\/g, '/');
      issues.push(`Module not found: '${specifier}' (resolved to '/${relResolved}') in ${relPath}`);
      continue;
    }

    // Namespace import: 只要模块物理存在即可
    if (namespaceImport && !defaultImport && !namedImportsBlock) {
      continue;
    }

    const exports = getModuleExports(resolvedPath, rootDirectory, cache);
    if (!exports) continue;

    if (defaultImport && !exports.has('default')) {
      issues.push(`The requested module '${specifier}' does not provide a default export (imported as '${defaultImport}' in ${relPath})`);
    }

    if (namedImportsBlock) {
      const cleanBlock = namedImportsBlock.replace(/[{}]/g, '');
      const items = cleanBlock.split(',').map(s => s.trim()).filter(Boolean);
      for (const item of items) {
        const parts = item.split(/\s+as\s+/);
        const importedName = parts[0].trim();
        if (!exports.has(importedName)) {
          issues.push(`The requested module '${specifier}' does not provide an export named '${importedName}' in ${relPath}`);
        }
      }
    }
  }

  // 校验动态 import('...') 静态字面量路径
  const dynamicImportRegex = /import\s*\(\s*['"]([^'"]+)['"]\s*\)/g;
  for (const match of source.matchAll(dynamicImportRegex)) {
    const specifier = match[1];
    const resolvedPath = resolveModuleSpecifier(rootDirectory, fullPath, specifier, importMap);
    if (!resolvedPath) continue;
    if (!fs.existsSync(resolvedPath)) {
      const relResolved = path.relative(rootDirectory, resolvedPath).replace(/\\/g, '/');
      issues.push(`Missing dynamic import target: '${specifier}' (resolved to '/${relResolved}') in ${relPath}`);
    }
  }

  return issues;
}

export function checkComponentPropRules(source, relPath) {
  const issues = [];
  const validSpaceSizes = new Set(['small', 'medium', 'large']);

  // 1. Template validation: <n-space ... size="..." ...>
  for (const m of source.matchAll(/<n-space\b([^>]*)>/gi)) {
    const sizeMatch = m[1].match(/\bsize=["']([^"']+)["']/i);
    if (sizeMatch) {
      const val = sizeMatch[1].trim();
      if (!validSpaceSizes.has(val) && isNaN(Number(val))) {
        issues.push(`Invalid size="${val}" on <n-space> in ${relPath}: Naive UI Space only supports 'small'|'medium'|'large' or number/[number, number]. Passing '${val}' crashes at runtime (reading 'split'/'horizontal').`);
      }
    }
  }

  // 2. Render function validation: h(NSpace, { size: '...' })
  for (const m of source.matchAll(/h\s*\(\s*NSpace\s*,\s*\{([^}]*)\}/g)) {
    const sizeMatch = m[1].match(/\bsize\s*:\s*['"]([^'"]+)['"]/);
    if (sizeMatch) {
      const val = sizeMatch[1].trim();
      if (!validSpaceSizes.has(val) && isNaN(Number(val))) {
        issues.push(`Invalid size: '${val}' in h(NSpace, ...) in ${relPath}: Naive UI Space only supports 'small'|'medium'|'large' or number. Passing '${val}' crashes at runtime (reading 'split'/'horizontal').`);
      }
    }
  }

  return issues;
}

export function runQualityGate(rootDirectory) {
  let files = 0, templates = 0;
  const errors = [];
  const importMap = extractImportMap(rootDirectory);
  const moduleExportsCache = new Map();

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

      // 2. ESM Import & Named Export Contract Verification (含 importmap 映射及具名导出符号检验)
      const importErrors = checkModuleImports(rootDirectory, full, source, importMap, moduleExportsCache);
      for (const err of importErrors) {
        errors.push(err);
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

      // 4. Returned keys existence check (verify __returned__ does not shorthand undeclared identifiers causing ReferenceError at runtime)
      const undeclaredReturned = checkReturnedScope(source);
      if (undeclaredReturned.length > 0) {
        errors.push(`Undeclared identifier(s) in ${relPath} __returned__: [${undeclaredReturned.join(', ')}] will throw ReferenceError at runtime`);
      }

      // 5. Component prop contract & enum validation (Naive UI NSpace size enum, preventing 'split'/'horizontal' crashes)
      const propErrors = checkComponentPropRules(source, relPath);
      for (const err of propErrors) {
        errors.push(err);
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
  console.log(`Validated ${files} JS modules and ${templates} executable Vue templates (AST symbol bindings & setup exports verified); no build output generated.`);
}
