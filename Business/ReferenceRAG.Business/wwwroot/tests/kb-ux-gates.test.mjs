import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import test from 'node:test';

const VIEW = new URL('../app/views/KnowledgeBases.js', import.meta.url);
const read = () => fs.readFileSync(VIEW, 'utf8');

/**
 * G-3 门禁：知识库卡片与创建向导必须呈现「可读身份」，不得直接把内部规格 ID 当主标签。
 * 背景：新建库会固化 spec_emb_<kbId> / spec_chunk_<kbId> 快照，卡片原样渲染 ID，
 * 用户无法判断实际绑定的是哪个模型；且「系统默认模板」提示的模型可能与真实固化值不符。
 */
test('G-3 KB 卡片绑定模型标签必须经 kbModelLabel 映射，不得直出 embeddingSpecId', () => {
    const src = read();
    const template = src.slice(src.indexOf('component.template = `'));
    const boundModelBlock = template.slice(
        template.indexOf('label="绑定模型"'),
        template.indexOf('label="分段策略"')
    );
    assert.ok(boundModelBlock.includes('kbModelLabel(kb)'), '绑定模型标签应调用 kbModelLabel');
    assert.ok(
        !/{{[^}]*kb\.embeddingSpecId[^}]*}}/.test(boundModelBlock.replace(/kb\.embeddingSpecId === 'spec_lexical_only'/g, '')),
        '绑定模型标签不得直接把 embeddingSpecId 作为显示文本'
    );
    assert.match(boundModelBlock, /kbModelHint\(kb\)/, '内部规格 ID 应降级到 tooltip 提示');
});

test('G-3 分段策略标签必须经 chunkingSpecLabel 映射', () => {
    const src = read();
    const template = src.slice(src.indexOf('component.template = `'));
    const chunkBlock = template.slice(template.indexOf('label="分段策略"'), template.indexOf('label="连接器通道"'));
    assert.ok(chunkBlock.includes('chunkingSpecLabel(kb)'), '分段策略应显示策略名');
    assert.ok(!/{{[^}]*kb\.chunkingSpecId[^}]*}}/.test(chunkBlock), '分段策略不得直出裸 spec id');
});

test('G-3 系统默认模板提示必须取自 /index/models/current 而非占位 default-spec 条目', () => {
    const src = read();
    assert.ok(src.includes("indexApi.getCurrentModel()"), '应读取真实当前模型');
    assert.ok(src.includes('currentGlobalModel'), '应持有当前全局模型状态');
    const label = src.slice(src.indexOf('const defaultTemplateModelLabel'), src.indexOf('const localSpecs'));
    assert.ok(label.includes('currentGlobalModel.value'), '提示标签优先使用真实当前模型');
});

test('G-4 源管理挂载下拉的知识库列表必须在激活时静默重拉（keep-alive 缓存实例）', () => {
    const src = fs.readFileSync(new URL('../app/views/Sources.js', import.meta.url), 'utf8');
    const activated = src.slice(src.indexOf('onActivated('), src.indexOf('const __returned__'));
    assert.ok(activated.includes('refreshSourceAndKbOptions'), 'onActivated 必须刷新源与知识库选项');
    assert.ok(!/if \(knowledgeBases\.value\.length === 0\)/.test(src), '不得再依赖 length===0 的条件式懒加载');
    const helper = src.slice(src.indexOf('const refreshSourceAndKbOptions'), src.indexOf('const handleCreateSource'));
    assert.ok(helper.includes('loadKnowledgeBases(true)'), '刷新器需静默重拉知识库列表');
    assert.ok(src.includes("registerRefresher('knowledge_bases'"), '应订阅 knowledge_bases 失效标签');
});

test('G-2 模型来源模式必须使用下拉而非拼接按钮组（避免固定宽度溢出）', () => {
    const src = read();
    const template = src.slice(src.indexOf('component.template = `'));
    assert.ok(!/<n-radio-group[^>]*bindingMode/.test(template), '不得在创建向导中使用 radio-group 承载 4 个长选项');
    assert.ok(template.includes('binding-mode-select'), '应使用带稳定类名的下拉控件');
    assert.ok(src.includes('bindingModeOptions'), '选项文案集中定义');
});
