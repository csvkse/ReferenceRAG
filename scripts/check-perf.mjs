// G-M8 性能预算门禁（方案 §7 / §8）。
// 静态部分（无需服务）：importmap ?v= 版本一致性、vendor 直引禁令、manifest/icons、
// Program.cs 压缩中间件顺序与 immutable 缓存头。
// 网络部分（--base-url 或 PERF_BASE_URL，隔离 WebHost）：原始 socket 计量真实传输字节
// （node:http 不自动解压），断言 br/gzip 命中、naive-ui ≤500KB、首屏 JS ≤800KB、
// vendor immutable / app no-cache、ETag 304 零字节。
// 用法：node scripts/check-perf.mjs [--static-only] [--base-url http://127.0.0.1:5199]
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WWW = path.join(ROOT, 'Business', 'ReferenceRAG.Business', 'wwwroot');

const argv = process.argv.slice(2);
const staticOnly = argv.includes('--static-only');
const baseUrlArg = (() => {
  const i = argv.indexOf('--base-url');
  if (i !== -1) return argv[i + 1];
  const eq = argv.find(a => a.startsWith('--base-url='));
  return eq ? eq.slice('--base-url='.length) : process.env.PERF_BASE_URL;
})();

const NAIVE_UI_BUDGET = 500 * 1024;
const FIRST_SCREEN_BUDGET = 800 * 1024;

let failures = 0;
function report(ok, name, detail) {
  if (!ok) failures++;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  → ' + detail : ''}`);
}

function walkJsFiles(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walkJsFiles(full, out);
    else if (entry.name.endsWith('.js')) out.push(full);
  }
  return out;
}

// ── 静态检查 ──────────────────────────────────────────────
const html = fs.readFileSync(path.join(WWW, 'index.html'), 'utf8');
const importMap = JSON.parse(html.match(/<script type="importmap">\s*([\s\S]*?)<\/script>/)[1]).imports;

const vendorEntries = Object.entries(importMap).filter(([, v]) => v.startsWith('/vendor/') && v.split('?')[0].endsWith('.js'));
const versions = new Set(vendorEntries.map(([, v]) => (v.match(/\?v=(\d+)$/) || [])[1]));
report(vendorEntries.length > 0 && versions.size === 1 && !versions.has(undefined),
  'importmap vendor 全部携带一致 ?v= 版本', [...versions].map(v => 'v=' + v).join(','));
const vendorVersion = [...versions][0];

const classicScript = html.match(/<script src="(\/vendor\/[^"]+)"><\/script>/);
report(!!classicScript && classicScript[1] === `/vendor/signalr.js?v=${vendorVersion}`,
  'signalr 经典脚本 ?v= 与 importmap 一致', classicScript?.[1]);

for (const [, v] of vendorEntries) {
  const file = path.join(WWW, v.split('?')[0]);
  report(fs.existsSync(file), `vendor 物理文件存在 ${v.split('?')[0]}`);
}

// 应用代码禁止相对/绝对路径直引 vendor：immutable 强缓存下无版本查询的 URL
// 在 ?v= bump 后仍命中陈旧缓存，必须统一走 importmap bare specifier。
const IMPORT_RE = /(?:^|[\n;])\s*import\s+(?:[^'";]*?\sfrom\s*)?['"]([^'"]+)['"]/g;
const EXPORT_RE = /(?:^|[\n;])\s*export\s+[^'";]*?\sfrom\s*['"]([^'"]+)['"]/g;
const appFiles = [path.join(WWW, 'app.js'), ...['app', 'core', 'shared'].flatMap(d => walkJsFiles(path.join(WWW, d)))];
const directVendor = [];
for (const file of appFiles) {
  const src = fs.readFileSync(file, 'utf8');
  for (const re of [IMPORT_RE, EXPORT_RE]) {
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(src))) {
      if (/(?:^|\/)vendor\/[^'"]*\.js/.test(m[1])) directVendor.push(`${path.relative(WWW, file)} → ${m[1]}`);
    }
  }
}
report(directVendor.length === 0, '应用代码 vendor 引用全部走 importmap（无路径直引）', directVendor.join('; ') || `${appFiles.length} 个模块扫描`);

const manifestPath = path.join(WWW, 'manifest.webmanifest');
let manifestOk = fs.existsSync(manifestPath);
let manifestDetail = 'manifest.webmanifest 缺失';
if (manifestOk) {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const missing = (manifest.icons || []).filter(i => !fs.existsSync(path.join(WWW, i.src)));
  manifestOk = manifest.display === 'standalone' && !!manifest.theme_color && (manifest.icons || []).length >= 2 && missing.length === 0;
  manifestDetail = `display=${manifest.display}, icons=${(manifest.icons || []).length}, missing=${missing.length}`;
}
report(manifestOk, 'PWA manifest 与图标齐备', manifestDetail);
report(/<link rel="manifest" href="\/manifest.webmanifest">/.test(html) && /name="theme-color"/.test(html),
  'index.html manifest/theme-color  meta 齐备');

const program = fs.readFileSync(path.join(ROOT, 'Host', 'ReferenceRAG.WebHost', 'Program.cs'), 'utf8');
const idxAdd = program.indexOf('AddResponseCompression');
const idxUse = program.indexOf('app.UseResponseCompression()');
const idxStatic = program.indexOf('app.UseStaticFiles(');
report(idxAdd !== -1 && idxUse !== -1 && idxStatic !== -1 && idxUse < idxStatic,
  'Program.cs 压缩中间件先于 StaticFiles', `UseResponseCompression@${idxUse} < UseStaticFiles@${idxStatic}`);
report(program.includes('public,max-age=31536000,immutable'), 'Program.cs vendor immutable 缓存头存在');

// ── 首屏 JS 集合：importmap 全部 .js + 经典 signalr + 启动静态闭包 ──
// app.js 启动即 await App.js / router/index.js（mount 前必达）；视图为懒加载动态 import，不计入。
function resolveWebPath(spec, importer) {
  if (importMap[spec]) return importMap[spec];
  if (spec.startsWith('/')) return spec;
  if (spec.startsWith('.')) return path.posix.normalize(path.posix.join(path.posix.dirname(importer), spec));
  return null;
}
const firstScreen = new Set();
for (const v of Object.values(importMap)) if (v.split('?')[0].endsWith('.js')) firstScreen.add(v);
if (classicScript) firstScreen.add(classicScript[1]);
const queue = ['/app.js', '/app/App.js', '/app/router/index.js'];
while (queue.length) {
  const url = queue.shift();
  if (firstScreen.has(url)) continue;
  firstScreen.add(url);
  const file = path.join(WWW, url.split('?')[0]);
  if (!fs.existsSync(file)) continue;
  const src = fs.readFileSync(file, 'utf8');
  for (const re of [IMPORT_RE, EXPORT_RE]) {
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(src))) {
      const resolved = resolveWebPath(m[1], url);
      if (resolved && resolved.split('?')[0].endsWith('.js')) queue.push(resolved);
    }
  }
}

console.log(`\n首屏 JS 集合（${firstScreen.size} 个资源）: ${[...firstScreen].join(', ')}\n`);

if (staticOnly || !baseUrlArg) {
  console.log(staticOnly ? '\n静态检查完成（--static-only）。' : '\n未提供 --base-url / PERF_BASE_URL，网络预算检查跳过。');
  process.exit(failures ? 1 : 0);
}

// ── 网络检查：原始计量（不自动解压） ────────────────────────
function rawGet(urlPath, extraHeaders = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(urlPath, baseUrlArg);
    const mod = u.protocol === 'https:' ? https : http;
    const req = mod.request(u, { headers: { 'Accept-Encoding': 'br, gzip', ...extraHeaders } }, res => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => {
        const body = Buffer.concat(chunks);
        resolve({ status: res.statusCode, headers: res.headers, bytes: body.length });
      });
    });
    req.setTimeout(20000, () => req.destroy(new Error('timeout ' + urlPath)));
    req.on('error', reject);
    req.end();
  });
}

const kb = n => (n / 1024).toFixed(1) + ' KB';
const isCompressed = h => ['br', 'gzip'].includes(h['content-encoding']);
const isImmutable = h => /public/.test(h['cache-control'] || '') && /max-age=31536000/.test(h['cache-control'] || '') && /immutable/.test(h['cache-control'] || '');

try {
  const index = await rawGet('/index.html');
  report(index.status === 200 && isCompressed(index.headers), 'index.html 200 + br/gzip', `${index.status}, ${index.headers['content-encoding']}, ${kb(index.bytes)}`);
  report(/no-cache/.test(index.headers['cache-control'] || ''), 'index.html no-cache', index.headers['cache-control']);

  let totalJs = 0;
  let naiveBytes = 0;
  for (const url of [...firstScreen].sort()) {
    const r = await rawGet(url);
    totalJs += r.bytes;
    if (url.split('?')[0] === '/vendor/naive-ui.js') naiveBytes = r.bytes;
    report(r.status === 200 && isCompressed(r.headers), `首屏压缩 ${url}`, `${r.status}, ${r.headers['content-encoding']}, ${kb(r.bytes)}`);
    if (url.startsWith('/vendor/')) {
      report(isImmutable(r.headers), `vendor immutable ${url}`, r.headers['cache-control']);
    }
  }
  report(naiveBytes > 0 && naiveBytes <= NAIVE_UI_BUDGET, `naive-ui 传输 ≤ ${kb(NAIVE_UI_BUDGET)}`, kb(naiveBytes));
  report(totalJs <= FIRST_SCREEN_BUDGET, `首屏 JS 总传输 ≤ ${kb(FIRST_SCREEN_BUDGET)}`, kb(totalJs));

  const css = await rawGet('/styles/pages.css');
  report(css.status === 200 && isCompressed(css.headers), 'pages.css 200 + br/gzip', `${css.headers['content-encoding']}, ${kb(css.bytes)}`);
  report(/no-cache/.test(css.headers['cache-control'] || ''), 'pages.css no-cache', css.headers['cache-control']);

  const appJs = await rawGet('/app.js');
  report(/no-cache/.test(appJs.headers['cache-control'] || '') && !/immutable/.test(appJs.headers['cache-control'] || ''),
    'app.js no-cache（非 immutable）', appJs.headers['cache-control']);

  const vendorUrl = `/vendor/vue.js?v=${vendorVersion}`;
  const first = await rawGet(vendorUrl);
  const etag = first.headers.etag;
  if (etag) {
    const revalidated = await rawGet(vendorUrl, { 'If-None-Match': etag });
    report(revalidated.status === 304 && revalidated.bytes === 0, 'vendor ETag 重验证 304 零字节', `${revalidated.status}, ${revalidated.bytes} B`);
  } else {
    report(false, 'vendor ETag 重验证 304 零字节', '响应缺少 ETag');
  }

  const manifestResp = await rawGet('/manifest.webmanifest');
  report(manifestResp.status === 200, 'manifest.webmanifest 200', `${manifestResp.status}, ${manifestResp.headers['content-type']}`);
  for (const icon of ['/icons/icon-192.png', '/icons/icon-512.png', '/icons/icon-maskable-512.png']) {
    const r = await rawGet(icon);
    report(r.status === 200 && /image\/png/.test(r.headers['content-type'] || ''), `PWA 图标 ${icon}`, `${r.status}, ${kb(r.bytes)}`);
  }

  console.log(`\n首屏 JS 总传输实测 ${kb(totalJs)}（预算 ${kb(FIRST_SCREEN_BUDGET)}）；naive-ui ${kb(naiveBytes)}（预算 ${kb(NAIVE_UI_BUDGET)}）。`);
} catch (err) {
  report(false, '网络预算检查执行', err.message);
}

console.log(failures ? `\nG-M8 失败：${failures} 项未达标。` : '\nG-M8 全部达标。');
process.exit(failures ? 1 : 0);