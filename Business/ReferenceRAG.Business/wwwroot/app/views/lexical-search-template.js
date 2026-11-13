// Search.js contains a generated template literal; keep this small extension separate.
export function enableLexicalSearchTemplate(template) {
    return template
        .replace('<n-radio-button value="Quick" label="快速" />',
            '<n-radio-button value="Quick" label="快速" />\n            <n-radio-button value="Lexical" label="词法" />')
        .replace('<!-- Advanced collapse -->', '<n-select v-model:value="selectedKbIds" :options="kbOptions" multiple clearable placeholder="选择知识库（默认检索全部知识库）" />\n        <!-- Advanced collapse -->')
        .replace('<n-button text size="small" @click="handleDrilldown(chunk)">', '<n-button v-if="!searchResponse.knowledgeBase" text size="small" @click="handleDrilldown(chunk)">')
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
        knowledgeBase: true, mode, chunks: (data.items || []).map(item => ({
            refId: item.chunkId, fileId: item.documentId, filePath: item.sourceKey,
            title: item.sourceKey, content: item.text, score: item.score, scoreKind: item.scoreKind, source: item.connectorId,
            locator: item.locator, startLine: item.startLine, endLine: item.endLine, headingPath: item.headingPath
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
