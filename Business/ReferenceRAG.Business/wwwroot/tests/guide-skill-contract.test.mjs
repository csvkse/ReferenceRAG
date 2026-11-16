import fs from 'node:fs';
import assert from 'node:assert/strict';
import test from 'node:test';

const GUIDE = new URL('../app/views/Guide.js', import.meta.url);
const SKILL = new URL('../../../../support/skill/ReferenceRAG/SKILL.md', import.meta.url);
const read = (u) => fs.readFileSync(u, 'utf8');

/**
 * G-DOC-1/4 门禁：Guide 与 Skill 的「契约一致性」。
 *
 * 背景：v2 重构后 Guide 仍列出 9 个不存在的 MCP 工具名（sources-list、
 * rag-semantic-search…），用户照抄必报"工具不存在"；且端口硬编码 7897，
 * 与实际部署（容器 5000 / 宿主 5123）不符。本门禁锁定两处不再漂移。
 */

const ACTUAL_MCP_TOOLS = [
  'knowledge_base_list', 'knowledge_base_search', 'knowledge_base_drill_down',
  'semantic_search', 'hybrid_search', 'lexical_search', 'rerank_results',
  'embed_text', 'embed_batch', 'calculate_similarity',
  'list_sources', 'get_source_info', 'get_index_status',
  'get_file_tree', 'read_note_content', 'get_note_outline',
  'search_file_content', 'find_files_by_pattern',
  'graph_search', 'graph_search_by_title',
  'ping', 'get_service_info', 'list_files', 'get_document_info', 'get_system_stats'
];

test('G-DOC-1 Guide 列出的 MCP 工具名必须全部真实存在', () => {
  const src = read(GUIDE);
  const listed = [...src.matchAll(/<td><n-text code>([a-z0-9_]+)<\/n-text><\/td>/g)]
    .map(m => m[1]);
  assert.ok(listed.length >= 8, `Guide 工具表条目过少：${listed.length}`);
  const unknown = listed.filter(n => !ACTUAL_MCP_TOOLS.includes(n));
  assert.deepEqual(unknown, [], `Guide 出现不存在的工具名：${unknown.join(', ')}`);
});

test('G-DOC-1 Guide 不再引用已废弃的破折号式工具名', () => {
  const src = read(GUIDE);
  for (const dead of ['sources-list', 'sources-get-info', 'rag-semantic-search', 'rag-hybrid-search',
                      'rag-rerank-results', 'embedding-encode-text', 'embedding-calculate-similarity',
                      'index-status']) {
    assert.ok(!src.includes(dead), `Guide 仍含废弃工具名 ${dead}`);
  }
});

test('G-DOC-4 Guide 的 MCP baseUrl 不再硬编码端口', () => {
  const src = read(GUIDE);
  assert.ok(src.includes('serviceOrigin'), 'MCP baseUrl 应使用运行时 origin');
  // docker 示例里的宿主端口可保留，但代码层不得写死 localhost:7897
  assert.ok(!src.includes('"baseUrl": "http://127.0.0.1:7897/api/mcp"'), 'MCP baseUrl 仍硬编码端口');
});

test('G-DOC-4 docker 端口映射与容器监听端口一致（5000）', () => {
  const src = read(GUIDE);
  assert.ok(!src.includes('"7897:7897"'), '端口映射应为 宿主:5000');
  assert.ok(src.includes(':5000'), '缺少 5000 容器端口映射');
});

test('G-DOC-2 Skill 文档的 API 路径都真实存在', () => {
  const skill = read(SKILL);
  const paths = [...skill.matchAll(/(\/api\/[A-Za-z0-9\/_-]+)/g)]
    .map(m => m[1].replace(/[.,;:)]+$/, ''))
    .filter(p => !p.includes('{'));
  assert.ok(paths.length > 0, '未解析到 API 路径');
  const KNOWN = [
    '/api/knowledge-bases', '/api/knowledge-base-search', '/api/ai/query',
    '/api/ai/drill-down', '/api/bm25index/search', '/api/graph/search',
    '/api/graph/stats', '/api/index/models/current', '/api/sources',
    '/api/sources/file/lines', '/api/sources/files/info', '/api/jobs'
  ];
  const unknown = paths.filter(p =>
    !KNOWN.includes(p) &&
    !p.startsWith('/api/knowledge-bases/') &&
    !p.startsWith('/api/jobs/') &&
    !p.startsWith('/api/index/'));
  assert.deepEqual([...new Set(unknown)], [], `Skill 引用未知 API：${[...new Set(unknown)].join(', ')}`);
});

test('G-DOC-3 Skill 不再包含易腐的实例快照', () => {
  const skill = read(SKILL);
  assert.ok(!skill.includes('当前实例状态'), 'SKILL.md 仍含实例状态快照');
  assert.ok(!/8 文件 \/ 8985 分块/.test(skill), 'SKILL.md 仍含过期文件数快照');
  assert.ok(skill.includes('knowledge-base-search'), 'Skill 决策树应指向跨库检索');
});

test('G-DOC-2 Skill 引用的 references 文件都存在', () => {
  const skill = read(SKILL);
  const root = new URL('../../../../support/skill/ReferenceRAG/', import.meta.url);
  const refs = [...skill.matchAll(/\[references\/([^\]]+\.md)\]/g)].map(m => m[1]);
  assert.ok(refs.length > 0);
  for (const r of refs) {
    assert.ok(fs.existsSync(new URL(`references/${r}`, root)), `缺少 references/${r}`);
  }
});
