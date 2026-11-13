// Search.js contains a generated template literal; keep this small extension separate.
export function enableLexicalSearchTemplate(template) {
    return template
        .replace('<n-radio-button value="Quick" label="快速" />',
            '<n-radio-button value="Quick" label="快速" />\n            <n-radio-button value="Lexical" label="词法" />')
        .replace('<!-- Advanced collapse -->', '<n-select v-model:value="selectedKbIds" :options="kbOptions" multiple clearable placeholder="选择知识库（默认检索全部知识库）" />\n        <!-- Advanced collapse -->')
        .replace('<n-button text size="small" @click="handleDrilldown(chunk)">', '<n-button v-if="!searchResponse.knowledgeBase" text size="small" @click="handleDrilldown(chunk)">')
        // 宽松兜底提示：地板层结果与精确命中在界面上完全一样，必须显式区分，
        // 否则用户会把"回落虚词"的结果当成真的匹配到了自己的查询。
        .replace('<n-tag size="small">耗时 {{ searchResponse.stats.durationMs }}ms</n-tag>',
            '<n-tag v-if="searchResponse.usedRecallFloor" type="warning" size="small" round title="查询的内容词在本知识库中不存在，以下为回落高频虚词的宽松结果">⚠ 宽松兜底</n-tag>\n          <n-tag size="small">耗时 {{ searchResponse.stats.durationMs }}ms</n-tag>')
        // 逐条覆盖率标签：覆盖率 = 命中锚点数 / 锚点总数。< 20% 基本等于"只是碰巧共享了虚词"。
        .replace('<n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">',
            '<n-tag v-if="chunk.coverage != null && chunk.coverage < 0.2" type="warning" size="small" :bordered="false" round :title="`查询词覆盖率 ${Math.round(chunk.coverage * 100)}%`">宽松匹配 {{ Math.round(chunk.coverage * 100) }}%</n-tag>\n                <n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">')
        .replace('<n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">',
            '<n-tag v-if="searchResponse?.stats?.isDegraded" type="warning" size="small" title="容灾降级">词法降级</n-tag>\n            <n-tag v-if="searchResponse.mode !== \'Lexical\' && searchResponse.mode !== 5" :type="getScoreType(chunk.score)" size="small" title="融合分数">');
}

export function knowledgeBaseRequest(query, ids, topK, mode, folders = []) {
    const payload = { query, knowledgeBaseIds: [...ids], topK, useLexicalTokenizer: mode === 'Lexical' };
    if (folders && folders.length > 0) {
        payload.folders = folders;
    }
    return payload;
}

export function knowledgeBaseResponse(data, mode) {
    const elapsed = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(data.elapsed || '');
    const durationMs = elapsed ? ((+(elapsed[1] || 0) * 24 + +elapsed[2]) * 3600 + +elapsed[3] * 60 + +elapsed[4]) * 1000 : 0;
    const isEarlyPruned = !!(data.earlyPruned || (mode !== 'Lexical' && (data.items || []).length > 0 && (data.items || []).every(i => i.scoreKind === 'BM25' || i.scoreKind === 2)));
    return {
        knowledgeBase: true, mode,
        // 宽松兜底标记：锚点内容词在语料中不存在时，后端会回落到虚词层，
        // 这里必须透出，否则这些结果在界面上与精确命中完全一样。
        usedRecallFloor: !!data.usedRecallFloor,
        chunks: (data.items || []).map(item => ({
            refId: item.chunkId, fileId: item.documentId, filePath: item.sourceKey,
            title: item.sourceKey, content: item.text, score: item.score, scoreKind: item.scoreKind, source: item.connectorId,
            locator: item.locator, startLine: item.startLine, endLine: item.endLine, headingPath: item.headingPath,
            // 查询词覆盖率（0~1）；null 表示本次未计算（短查询无区分度）
            coverage: item.coverage ?? null,
            isFloor: !!item.isFloor
        })), files: [], context: '', stats: {
            durationMs: Math.round(durationMs),
            estimatedTokens: 0,
            // 模型级降级（断路器/推理失败）与索引级降级（词法索引补齐中）都要如实透出
            isDegraded: !!(data.degraded || data.indexDegraded),
            degradeReason: data.degradationReason || null,
            isEarlyPruned,
            // 后端跨库检索的全链路耗时链（扇出 / 各库召回 / 融合），用于瀑布图
            timingChain: data.timingChain || null
        }
    };
}
