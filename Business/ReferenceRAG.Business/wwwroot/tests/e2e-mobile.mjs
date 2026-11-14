// G-M9 三视口 E2E 验收（方案 §7）：375×667 / 768×1024 / 1440×900。
// 黄金路径：KB 卡片 → 绑定模型弹窗 → 入队 → 抽屉推送进度 → 成功 → 回滚；
// 取消路径：再次入队 → 运行中取消 → Cancelled 终态；
// 每步断言无横向溢出（scrollWidth ≤ innerWidth）、控制台零错误；
// 传输策略：推送在线时轮询≈心跳、hidden 标签页 0 请求、恢复对账。
// 可见性以 DOM 事件层仿真（覆盖 document.hidden + 派发 visibilitychange）验证 JobCenter P1-4。
// 自编排：mock 嵌入 5198 + 隔离 WebHost 5199（全新数据目录）+ seed 60 文档。
// 前置：dotnet build Host/ReferenceRAG.WebHost（Debug）；npm i --no-package-lock（playwright-core + 本机 Edge）。
// 运行：npm run e2e:mobile（wwwroot 下）。E2E_NO_SPAWN=1 + E2E_KB_ID 可复用外部实例。
import { chromium } from 'playwright-core';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../../../..');
const BASE = process.env.E2E_BASE_URL || 'http://127.0.0.1:5199';
const MOCK = 'http://127.0.0.1:5198';
const SHOTS = path.join(ROOT, 'bin', 'acceptance', '.run', 'shots');
fs.mkdirSync(SHOTS, { recursive: true });

const VIEWPORTS = [
  { name: 'mobile', width: 375, height: 667 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'desktop', width: 1440, height: 900 },
];

let failures = 0;
const ok = (cond, name, detail = '') => {
  if (!cond) failures++;
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  → ' + detail : ''}`);
};

async function waitHttp(url, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { const r = await fetch(url); if (r.status < 500) return true; } catch {}
    await new Promise(r => setTimeout(r, 500));
  }
  return false;
}

const spawned = [];
async function orchestrate() {
  if (process.env.E2E_NO_SPAWN === '1') return process.env.E2E_KB_ID;
  try { await fetch(MOCK + '/__stats'); } catch {
    const p = spawn(process.execPath, [path.join(ROOT, 'bin', 'acceptance', 'mock-embeddings.mjs')], { stdio: 'ignore' });
    spawned.push(p);
    if (!await waitHttp(MOCK + '/__stats', 15000)) throw new Error('mock embeddings 未就绪');
  }
  const runDir = path.join(ROOT, 'bin', 'acceptance', '.run', 'e2e-' + Date.now());
  fs.mkdirSync(path.join(runDir, 'data'), { recursive: true });
  fs.mkdirSync(path.join(runDir, 'logs'), { recursive: true });
  const dll = path.join(ROOT, 'Host', 'ReferenceRAG.WebHost', 'bin', 'Debug', 'net10.0', 'ReferenceRAG.WebHost.dll');
  const host = spawn('dotnet', [dll], {
    stdio: 'ignore',
    env: { ...process.env, REFERENCERAG_DATA_PATH: path.join(runDir, 'data'), REFERENCERAG_LOGS_PATH: path.join(runDir, 'logs'), urls: BASE },
  });
  spawned.push(host);
  if (!await waitHttp(BASE + '/api/auth/check')) throw new Error('隔离 WebHost 未就绪');
  const seed = spawn(process.execPath, [path.join(ROOT, 'bin', 'acceptance', 'seed.mjs')], { env: { ...process.env } });
  let out = '';
  seed.stdout.on('data', d => out += d);
  seed.stderr.on('data', d => out += d);
  await new Promise((res, rej) => { seed.on('exit', c => c === 0 ? res() : rej(new Error('seed 失败: ' + out))); });
  const kbId = (out.match(/KB_ID=(\S+)/) || [])[1];
  if (!kbId) throw new Error('seed 未输出 KB_ID');
  console.log('隔离实例就绪，KB_ID=' + kbId);
  return kbId;
}

// 作业对账请求口径：JobCenter 双源各发一次（/api/index/jobs + /api/jobs），
// 故一次心跳 ≈2 个请求；/reindex/status 是一次性代次读取，不计入轮询。
const isPolling = url => /\/api\/index\/jobs(\?|$)|\/api\/jobs(\?|$)/.test(url);

// Windows/Edge 下无法让真实窗口触发 visibilityState=hidden（headless 恒 visible；
// CDP 最小化不触发遮挡重算），故在 DOM 事件层仿真：覆盖 document.hidden/
// visibilityState 并派发真实 visibilitychange，直接驱动生产代码
// JobCenter._bindLifecycle 的 P1-4 策略路径。
const VIS_INIT = () => {
  const realHidden = Object.getOwnPropertyDescriptor(Document.prototype, 'hidden');
  let hidden = false;
  Object.defineProperty(document, 'hidden', {
    get() { return hidden || (realHidden ? realHidden.get.call(this) : false); },
    configurable: true,
  });
  Object.defineProperty(document, 'visibilityState', {
    get() { return hidden ? 'hidden' : 'visible'; },
    configurable: true,
  });
  window.__setVis = (state) => {
    hidden = state === 'hidden';
    document.dispatchEvent(new Event('visibilitychange'));
  };
};
const setVisibility = (page, state) => page.evaluate(s => window.__setVis(s), state);

async function runViewport(browser, vp, kbId) {
  const context = await browser.newContext({ viewport: { width: vp.width, height: vp.height }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const consoleErrors = [];
  const pageErrors = [];
  const badResponses = [];
  const requests = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', e => pageErrors.push(String(e)));
  page.on('response', r => { if (r.status() >= 400) badResponses.push(`${r.status()} ${r.url()}`); });
  page.on('request', r => requests.push({ url: r.url(), t: Date.now() }));
  const shot = async name => page.screenshot({ path: path.join(SHOTS, `${vp.name}-${name}.png`) });
  const overflow = async label => {
    const m = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, iw: window.innerWidth, bw: document.body.scrollWidth }));
    ok(m.sw <= m.iw + 1 && m.bw <= m.iw + 1, `[${vp.name}] 无横向溢出 ${label}`, `scrollWidth=${m.sw}/${m.bw} innerWidth=${m.iw}`);
  };
  const click = async (locator, name) => {
    await locator.waitFor({ state: 'visible', timeout: 20000 });
    await locator.click();
  };

  try {
    await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
    await page.getByText('知识库', { exact: false }).first().waitFor({ timeout: 30000 });
    await page.waitForTimeout(1200);
    await overflow('dashboard');
    await shot('01-dashboard');

    for (const [route, label] of [['/sources', 'sources'], ['/models', 'models']]) {
      await page.goto(BASE + route, { waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(1500);
      await overflow(label);
      if (vp.name === 'mobile') {
        const cards = await page.locator('.mobile-card-list').count();
        const tables = await page.locator('.n-data-table').count();
        ok(cards >= 1 && tables === 0, `[${vp.name}] ${label} 移动态卡片化`, `cards=${cards} tables=${tables}`);
      }
      await shot('02-' + label);
    }

    await page.goto(BASE + '/knowledge-bases', { waitUntil: 'domcontentloaded' });
    const card = page.locator('.n-card').filter({ hasText: '验收词法库' }).first();
    await card.waitFor({ timeout: 30000 });
    await page.waitForTimeout(800);
    await overflow('kb-list');
    await shot('03-kb-list');

    // ── 黄金路径：绑定 → 入队 → 推送进度 → 成功 → 回滚 ──
    await click(card.getByRole('button', { name: '绑定模型' }), '绑定模型');
    const modal = page.locator('.n-modal').filter({ hasText: '确认绑定并计算切片向量' });
    await modal.waitFor({ timeout: 15000 });
    await overflow('bind-modal');
    await shot('04-bind-modal');
    await modal.locator('.n-select').click();
    await click(page.locator('.n-base-select-option').filter({ hasText: 'acceptance-mock' }).first(), '选择 mock 规格');
    await click(modal.getByRole('button', { name: '确认绑定并计算切片向量' }), '确认绑定');

    const drawer = page.locator('.n-drawer').filter({ hasText: '重构索引进度' });
    await drawer.waitFor({ timeout: 20000 });
    await drawer.locator('.reindex-hero.is-running, .reindex-hero.is-queued').first().waitFor({ timeout: 20000 });
    await overflow('drawer-active');
    await shot('05-drawer-active');
    ok(await card.getByText('重构中', { exact: false }).count() > 0, `[${vp.name}] 卡片忙碌徽标`, '重构中 %');

    // 推送驱动：8s 窗口内进度 DOM 变化且轮询请求 ≤2（10s 心跳）
    const winStart = Date.now();
    const before = await drawer.locator('.reindex-stat b').first().textContent();
    let changed = false;
    try {
      await page.waitForFunction(prev => {
        const el = document.querySelector('.n-drawer .reindex-stat b');
        return el && el.textContent !== prev;
      }, before, { timeout: 10000, polling: 50 });
      changed = true;
    } catch {}
    const pollsInWindow = requests.filter(r => r.t >= winStart && r.t <= Date.now() && isPolling(r.url)).length;
    ok(changed, `[${vp.name}] 进度推送更新（无轮询驱动）`, `processed ${before} → 变化=${changed}`);
    ok(pollsInWindow <= 2, `[${vp.name}] 推送在线轮询≈心跳`, `8-10s 窗口轮询 ${pollsInWindow} 次`);

    await drawer.locator('.reindex-hero.is-succeeded').waitFor({ timeout: 120000 });
    await overflow('drawer-succeeded');
    await shot('06-drawer-succeeded');

    await click(drawer.getByRole('button', { name: '回滚至上一代' }), '回滚');
    await click(page.getByRole('button', { name: '确认回滚' }), '确认回滚');
    await drawer.getByRole('button', { name: '回滚至上一代' }).waitFor({ state: 'detached', timeout: 30000 });
    await page.waitForTimeout(800);
    await overflow('drawer-rolled-back');
    await shot('07-rolled-back');
    await click(drawer.getByRole('button', { name: '关闭' }), '关闭抽屉');
    await page.waitForTimeout(600);

    // ── 取消路径：再次入队 → 运行中取消 → Cancelled ──
    await click(card.getByRole('button', { name: '绑定模型' }), '再次绑定');
    const modal2 = page.locator('.n-modal').filter({ hasText: '确认绑定并计算切片向量' });
    await modal2.waitFor({ timeout: 15000 });
    await modal2.locator('.n-select').click();
    await click(page.locator('.n-base-select-option').filter({ hasText: 'acceptance-mock' }).first(), '选择 mock 规格');
    await click(modal2.getByRole('button', { name: '确认绑定并计算切片向量' }), '确认绑定');
    await drawer.locator('.reindex-hero.is-running').waitFor({ timeout: 30000 });
    await page.waitForFunction(() => {
      const el = document.querySelector('.n-drawer .reindex-stat b');
      return el && parseInt(el.textContent, 10) > 0;
    }, null, { timeout: 30000 });
    await click(drawer.getByRole('button', { name: '取消作业' }), '取消作业');
    await click(page.getByRole('button', { name: '确认取消' }), '确认取消');
    await drawer.locator('.reindex-hero.is-cancelled').waitFor({ timeout: 30000 });
    ok(await drawer.getByText('作业已取消', { exact: false }).count() > 0, `[${vp.name}] 取消终态快照`, 'Cancelled + 影子表清理提示');
    await overflow('drawer-cancelled');
    await shot('08-cancelled');
    await click(drawer.getByRole('button', { name: '关闭' }), '关闭抽屉');

    ok(consoleErrors.length === 0, `[${vp.name}] 控制台零错误`, consoleErrors.slice(0, 3).join(' | '));
    ok(pageErrors.length === 0, `[${vp.name}] 零未捕获异常`, pageErrors.slice(0, 3).join(' | '));
    ok(badResponses.length === 0, `[${vp.name}] 零 4xx/5xx 响应`, badResponses.slice(0, 3).join(' | '));
  } catch (err) {
    ok(false, `[${vp.name}] 流程执行`, err.message.split('\n')[0]);
    await shot('99-error').catch(() => {});
  } finally {
    await context.close();
  }
}

// 传输策略（P1-4）：hidden 标签页 0 请求 / 恢复立即对账 / 推送在线心跳 ≤6 次每分钟。
// 可见性用 DOM 事件层仿真（见 VIS_INIT），headless 即可验证生产代码路径。
async function runTransportPolicy(kbId) {
  console.log('\n── 传输策略（headless + DOM 可见性仿真） ──');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  // 传输 phase：12s 心跳窗口需要 job 存活 ≥15s，1500ms/批 ≈ 18s，结束后恢复 800。
  await fetch(MOCK + '/__control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ delayMs: 1500 }) }).catch(() => {});
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    await context.addInitScript(VIS_INIT);
    const page = await context.newPage();
    const requests = [];
    const consoleErrors = [];
    page.on('request', r => requests.push({ url: r.url(), t: Date.now() }));
    page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
    await page.goto(BASE + '/knowledge-bases', { waitUntil: 'domcontentloaded' });
    await page.locator('.n-card').filter({ hasText: '验收词法库' }).first().waitFor({ timeout: 30000 });
    await page.waitForTimeout(2500);

    await setVisibility(page, 'hidden');
    const hiddenState = await page.evaluate(() => document.visibilityState);
    ok(hiddenState === 'hidden', '后台标签页 visibilityState=hidden', hiddenState);
    const hiddenStart = Date.now();
    await page.waitForTimeout(6000);
    const hiddenPolls = requests.filter(r => r.t >= hiddenStart && isPolling(r.url)).length;
    ok(hiddenPolls === 0, 'hidden 标签页 0 轮询', `6s 窗口 ${hiddenPolls} 次`);

    const resumeStart = Date.now();
    await setVisibility(page, 'visible');
    await page.waitForTimeout(3000);
    const resumeState = await page.evaluate(() => document.visibilityState);
    const resumePolls = requests.filter(r => r.t >= resumeStart && isPolling(r.url)).length;
    ok(resumeState === 'visible' && resumePolls >= 1 && resumePolls <= 4, '恢复可见立即对账', `visibility=${resumeState}, 3s 窗口 ${resumePolls} 次`);

    // 活跃作业 + 推送在线：12s 窗口心跳 ≤4 次（10s 节奏 ≈ 6 次/分钟上限，双源 ×1 tick）
    const card = page.locator('.n-card').filter({ hasText: '验收词法库' }).first();
    await card.getByRole('button', { name: '绑定模型' }).click();
    const modal = page.locator('.n-modal').filter({ hasText: '确认绑定并计算切片向量' });
    await modal.waitFor({ timeout: 15000 });
    await modal.locator('.n-select').click();
    await page.locator('.n-base-select-option').filter({ hasText: 'acceptance-mock' }).first().click();
    await modal.getByRole('button', { name: '确认绑定并计算切片向量' }).click();
    const drawer = page.locator('.n-drawer').filter({ hasText: '重构索引进度' });
    await drawer.locator('.reindex-hero.is-running').waitFor({ timeout: 30000 });
    const hbStart = Date.now();
    await page.waitForTimeout(12000);
    const hbPolls = requests.filter(r => r.t >= hbStart && isPolling(r.url)).length;
    ok(hbPolls <= 4, '推送在线心跳 ≤6 次/分钟', `12s 窗口 ${hbPolls} 次`);
    await drawer.getByRole('button', { name: '取消作业' }).click();
    await page.getByRole('button', { name: '确认取消' }).click();
    await drawer.locator('.reindex-hero.is-cancelled').waitFor({ timeout: 30000 });
    ok(consoleErrors.length === 0, '传输策略 phase 控制台零错误', consoleErrors.slice(0, 3).join(' | '));
    await context.close();
  } catch (err) {
    ok(false, '传输策略 phase 执行', err.message.split('\n')[0]);
  } finally {
    await fetch(MOCK + '/__control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ delayMs: 800 }) }).catch(() => {});
    await browser.close();
  }
}

const kbId = await orchestrate();
// 视口 phase：job 需活过推送观测窗口（~10s），300ms/批的 ~3.6s 太快，取 800ms/批。
await fetch(MOCK + '/__control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ delayMs: 800 }) }).catch(() => {});
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  for (const vp of VIEWPORTS) {
    console.log(`\n── 视口 ${vp.name} ${vp.width}×${vp.height} ──`);
    await runViewport(browser, vp, kbId);
  }
  await runTransportPolicy(kbId);
} finally {
  await browser.close();
  for (const p of spawned) p.kill();
}
console.log(failures ? `\nG-M9 失败：${failures} 项。` : '\nG-M9 三视口验收全部达标。');
console.log('截图目录：' + SHOTS);
process.exit(failures ? 1 : 0);