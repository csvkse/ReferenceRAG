import { knowledgeBasesApi } from '/app/api/index.js';

/**
 * 知识库绑定的可读标签。
 *
 * 知识库的 embeddingSpecId 是内部身份（系统默认模板会固化成 spec_emb_<kbId> 快照，
 * 服务商模型则是 prov_<provider>_<model>），直接渲染会给用户看到不可读的 ID。
 * 展示层统一走「模型名 (维度d)」，内部 ID 只允许出现在 tooltip / 详情视图。
 */

let specMapPromise = null;

export function resetKbSpecMap() {
    specMapPromise = null;
}

export async function loadKbSpecMap({ force = false } = {}) {
    if (!force && specMapPromise) return specMapPromise;
    specMapPromise = knowledgeBasesApi.getEmbeddingSpecs()
        .then(res => {
            const map = {};
            for (const spec of (res.data || [])) {
                if (spec?.id) map[spec.id] = spec;
            }
            return map;
        })
        .catch(() => {
            specMapPromise = null;
            return {};
        });
    return specMapPromise;
}

export function kbModelLabel(kb, specMap = {}) {
    const specId = kb?.embeddingSpecId || kb?.EmbeddingSpecId || '';
    if (!specId || specId === 'spec_lexical_only') return '纯词法 (FTS5 BM25)';

    const mapped = specMap[specId];
    const base = specMap[specId.replace(/^spec_emb_/, '')];
    const name = mapped?.modelName || mapped?.ModelName || base?.modelName || base?.ModelName;
    const dim = mapped?.dimension || mapped?.Dimension || base?.dimension || base?.Dimension;

    if (!name) return '默认规范';
    return dim ? `${name} (${dim}d)` : name;
}

export function kbOptionLabel(kb, specMap = {}, { prefix = '' } = {}) {
    return `${prefix}${kb?.name || kb?.Name || ''}（${kbModelLabel(kb, specMap)}）`;
}
