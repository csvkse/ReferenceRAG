// G-M5 移动端静态门禁（方案 §7）。
// 1. 双风格模板稳健提取（双引号单行 JSON + 反引号多行转义扫描）并全部过 Vue 编译器，
//    补齐 check.mjs 仅编译双引号模板的覆盖缺口（Graph/Guide 等反引号视图）。
// 2. n-drawer 必须有 isMobile 响应式 placement/width/height 策略。
// 3. n-modal 必须有 ≤95vw 的宽度上限（max-width 或 width 本身）。
// 4. n-grid cols 必须为响应式断点字符串（"1 s:2 m:4" 形态）。
//    白名单豁免：Performance.js（密集基准测试网格，桌面运维工具）、
//    ApiHelp.js（24 栏 API 文档布局，桌面阅读场景）——二者不在移动端验收路径内。
// 5. 禁止裸 100vh：CSS 仅允许紧跟同属性 100dvh 覆盖的降级 fallback；JS 一律禁止。
// 6. JS/CSS 断点常量锁定一致：BREAKPOINTS {mobile:768,tablet:1024} ↔ CSS 767/1023px。
// 用法：node scripts/check-mobile.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compile } from '../Business/ReferenceRAG.Business/wwwroot/vendor/vue.js';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WWW = path.join(ROOT, 'Business', 'ReferenceRAG.Business', 'wwwroot');

const GRID_WHITELIST = new Set(['app/views/Performance.js', 'app/views/ApiHelp.js']);

let failures = 0;
function report(ok, name, detail) {
  if (!ok) failures++;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  → ' + detail : ''}`);
}

function extractTemplates(src) {
  const out = [];
  const dq = src.match(/^component\.template = (".*");$/m);
  if (dq) { out.push(JSON.parse(dq[1])); return out; }
  const marker = 'component.template = `';
  const start = src.indexOf(marker);
  if (start !== -1) {
    let i = start + marker.length, buf = '';
    while (i < src.length) {
      const c = src[i];
      if (c === '\\') {
        const n = src[i + 1];
        buf += n === 'n' ? '\n' : n === 't' ? '\t' : n;
        i += 2; continue;
      }
      if (c === '`') break;
      buf += c; i++;
    }
    out.push(buf);
  }
  return out;
}

function walk(d, out = []) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const f = path.join(d, e.name);
    if (e.isDirectory()) { if (!['vendor', 'tests', 'node_modules', 'icons'].includes(e.name)) walk(f, out); }
    else if (e.name.endsWith('.js')) out.push(f);
  }
  return out;
}

function openingTags(template, tag) {
  const tags = [];
  const re = new RegExp('<' + tag + '(?=[\\s>])[^>]*>', 'g');
  let m;
  while ((m = re.exec(template))) tags.push(m[0]);
  return tags;
}

function vwCapOk(attrs) {
  const vwValues = [];
  const maxWidth = attrs.match(/max-width\s*:\s*([^;"']+)/i) || attrs.match(/maxWidth\s*:\s*'([^']+)'/);
  if (maxWidth) {
    const vws = [...maxWidth[1].matchAll(/(\d+(?:\.\d+)?)vw/g)].map(m => parseFloat(m[1]));
    if (vws.length === 0) return false; // px-only cap does not bound small viewports
    vwValues.push(...vws);
  } else {
    const width = attrs.match(/(?:^|[";{ ])width\s*:\s*([^;"']+)/i) || attrs.match(/width\s*:\s*'([^']+)'/);
    if (!width) return false;
    const vws = [...width[1].matchAll(/(\d+(?:\.\d+)?)vw/g)].map(m => parseFloat(m[1]));
    if (vws.length === 0) return false;
    vwValues.push(...vws);
  }
  return vwValues.every(v => v <= 95);
}

let compiled = 0;
const files = walk(WWW);
for (const f of files) {
  const rel = path.relative(WWW, f).replace(/\\/g, '/');
  const src = fs.readFileSync(f, 'utf8');
  for (const t of extractTemplates(src)) {
    try {
      compile(t, { decodeEntities: v => v });
      compiled++;
    } catch (err) {
      report(false, `模板编译 ${rel}`, err.message);
      continue;
    }

    for (const tag of openingTags(t, 'n-drawer')) {
      report(/isMobile/.test(tag), `n-drawer 响应式策略 ${rel}`, tag.replace(/\s+/g, ' ').slice(0, 120));
    }
    for (const tag of openingTags(t, 'n-modal')) {
      report(vwCapOk(tag), `n-modal ≤95vw 上限 ${rel}`, tag.replace(/\s+/g, ' ').slice(0, 120));
    }
    for (const tag of openingTags(t, 'n-grid')) {
      if (GRID_WHITELIST.has(rel)) continue;
      const cols = tag.match(/:?cols="([^"]+)"/);
      const ok = !!cols && /\b[sm]:\d+/.test(cols[1]);
      report(ok, `n-grid 响应式 cols ${rel}`, cols ? cols[1] : tag.slice(0, 80));
    }
  }
  if (/100vh/.test(src)) report(false, `JS 禁止裸 100vh ${rel}`, '需改用 100dvh');
}
console.log(`\n编译 ${compiled} 个模板（双引号 + 反引号全量）。`);

// CSS：100vh 仅允许紧跟同属性 dvh 覆盖；断点与 JS 常量一致。
const css = fs.readFileSync(path.join(WWW, 'styles/pages.css'), 'utf8');
const lines = css.split('\n');
let vhFallbacks = 0;
for (let i = 0; i < lines.length; i++) {
  if (!/100vh/.test(lines[i])) continue;
  const prop = (lines[i].match(/^\s*([a-z-]+)\s*:/) || [])[1];
  let j = i + 1;
  while (j < lines.length && lines[j].trim() === '') j++;
  const ok = prop && j < lines.length && lines[j].includes('100dvh') && lines[j].trim().startsWith(prop + ':');
  report(ok, `CSS 100vh dvh 降级 fallback pages.css:${i + 1}`, lines[i].trim());
  if (ok) vhFallbacks++;
}

const responsive = fs.readFileSync(path.join(WWW, 'core/responsive.js'), 'utf8');
const bp = responsive.match(/mobile:\s*(\d+),\s*tablet:\s*(\d+)/);
report(!!bp && bp[1] === '768' && bp[2] === '1024', 'JS BREAKPOINTS = 768/1024', bp ? `${bp[1]}/${bp[2]}` : '未找到');
const media = [...css.matchAll(/@media\s*\(\s*(max|min)-width:\s*(\d+)px\s*\)/g)];
let mobileQuerySeen = false;
for (const m of media) {
  const [, kind, px] = m;
  const n = parseInt(px, 10);
  let ok;
  if (kind === 'max') ok = n <= 767 || n === 1023;
  else ok = n === 768 || n === 1024;
  if (kind === 'max' && n === 767) mobileQuerySeen = true;
  report(ok, `CSS 断点与 JS 一致 @media (${kind}-width: ${px}px)`, n <= 767 && kind === 'max' && n !== 767 ? '子断点微调（<767）' : '');
}
report(mobileQuerySeen, 'CSS 主移动断点 767px 存在（= BREAKPOINTS.mobile - 1）');

console.log(failures ? `\nG-M5 失败：${failures} 项。` : '\nG-M5 全部达标。');
process.exit(failures ? 1 : 0);