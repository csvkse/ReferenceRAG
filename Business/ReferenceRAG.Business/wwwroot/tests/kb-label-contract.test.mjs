import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';

const read = (p) => fs.readFileSync(new URL(p, import.meta.url), 'utf8');

/**
 * G-DOC-5 门禁：知识库身份（embeddingSpecId）只允许出现在 tooltip / 详情，
 * 选择器与列表主标签必须走可读的「模型名 (维度d)」。
 *
 * 背景：默认模板会固化 spec_emb_<kbId>，服务商模型是 prov_<provider>_<model>。
 * 真机实测仪表盘表格与多个选择器直接渲染了 prov_prov_sf_BAAI/bge-m3 这类内部 ID。
 */

const VIEWS = {
  Dashboard: '../app/views/Dashboard.js',
  Chat: '../app/views/Chat.js',
  Search: '../app/views/Search.js',
  Graph: '../app/views/Graph.js',
  BM25Index: '../app/views/BM25Index.js',
  ApiKeys: '../app/views/ApiKeys.js'
};

test('G-DOC-5 共享可读标签模块存在且支持快照 ID 回退', () => {
  const src = read('../shared/kb-label.js');
  assert.ok(src.includes('export function kbModelLabel'), '应导出 kbModelLabel');
  assert.ok(src.includes('export function kbOptionLabel'), '应导出 kbOptionLabel');
  assert.ok(src.includes('spec_lexical_only'), '纯词法库需显示为「纯词法」');
  assert.ok(src.includes("replace(/^spec_emb_/"), '应支持 spec_emb_<kbId> 快照回退到全局规格');
});

for (const [name, path] of Object.entries(VIEWS)) {
  test(`G-DOC-5 ${name} 不得把裸 embeddingSpecId 作为显示文本`, () => {
    const src = read(path);
    const templateStart = src.indexOf('component.template = ');
    const template = templateStart >= 0 ? src.slice(templateStart) : '';
    const nakedInTemplate = /\{\{[^}]*\.embeddingSpecId[^}]*\}\}/.test(template);
    assert.ok(!nakedInTemplate, `${name} 模板仍直出 embeddingSpecId`);
    const labelCall = src.includes('kbModelLabel(') || src.includes('kbOptionLabel(');
    assert.ok(labelCall, `${name} 应调用共享可读标签`);
  });
}

test('G-DOC-5 仪表盘「总文件索引数」取自知识库连接器物理统计', () => {
  const src = read('../app/views/Dashboard.js');
  assert.ok(src.includes('totalKbFiles'), '应新增基于连接器的总文件数');
  assert.ok(!src.includes('stats?.totalFiles'), '/index/summary 的旧库快照不得再作为展示值');
});
