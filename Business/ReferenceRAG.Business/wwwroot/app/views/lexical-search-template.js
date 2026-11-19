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
            '<n-tag v-if="searchResponse.noMatch" type="error" size="small" round :title="searchResponse.noMatchReason || \'返回内容与查询无关\'">⚠ 未命中查询</n-tag>\n          <n-tag v-if="searchResponse.usedRecallFloor" type="warning" size="small" round title="查询的内容词在本知识库中不存在，以下为回落高频虚词的宽松结果">⚠ 宽松兜底</n-tag>\n          <n-tag v-if="searchResponse.kbChannels" size="small" :bordered="false" round :title="`各库召回通道：${Object.values(searchResponse.kbChannels).join(\' / \')}`">通道 {{ Object.values(searchResponse.kbChannels).some(c => c === \'hybrid\') ? \'向量+词法\' : \'词法\' }}</n-tag>\n          <n-tag size="small">耗时 {{ searchResponse.stats.durationMs }}ms</n-tag>')
        // 逐条覆盖率标签：覆盖率 = 命中锚点数 / 锚点总数。< 20% 基本等于"只是碰巧共享了虚词"。
        .replace('<n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">',
            '<n-tag v-if="chunk.coverage != null && chunk.coverage < 0.2" type="warning" size="small" :bordered="false" round :title="`查询词覆盖率 ${Math.round(chunk.coverage * 100)}%`">宽松匹配 {{ Math.round(chunk.coverage * 100) }}%</n-tag>\n                <n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">')
        .replace('<n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">',
            '<n-tag v-if="searchResponse?.stats?.isDegraded" type="warning" size="small" title="容灾降级">词法降级</n-tag>\n            <n-tag v-if="searchResponse.mode !== \'Lexical\' && searchResponse.mode !== 5" :type="getScoreType(chunk.score)" size="small" title="融合分数">');
}

export function knowledgeBaseRequest(query, ids, topK, mode, folders = []) {
    const isLexical = (mode || '').toLowerCase() === 'lexical';
    const payload = {
        query,
        knowledgeBaseIds: ids && ids.length > 0 ? [...ids] : [],
        topK,
        useLexicalTokenizer: isLexical
    };
    if (folders && folders.length > 0) {
        payload.folders = folders;
    }
    return payload;
}

export function knowledgeBaseResponse(data, mode) {
    const durationMs = data.elapsedMs || (data.elapsed ? (() => {
        const elapsed = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(data.elapsed || '');
        return elapsed ? ((+(elapsed[1] || 0) * 24 + +elapsed[2]) * 3600 + +elapsed[3] * 60 + +elapsed[4]) * 1000 : 0;
    })() : 0);
    const isEarlyPruned = !!(data.earlyPruned || (mode !== 'Lexical' && (data.items || []).length > 0 && (data.items || []).every(i => i.scoreKind === 'BM25' || i.scoreKind === 2)));
    const hits = data.hits || [];
    const items = data.items || [];
    const chunks = hits.length > 0
        ? hits.map(hit => ({
            refId: hit.chunkId || hit.citationRef?.chunkId,
            fileId: hit.documentId || hit.citationRef?.documentId,
            filePath: hit.sourceKey,
            title: hit.title || hit.sourceKey,
            content: hit.excerpt,
            score: hit.score,
            scoreKind: hit.scoreKind,
            source: hit.sourceKey,
            citationRef: hit.citationRef,
            startLine: hit.startLine,
            endLine: hit.endLine,
            headingPath: hit.headingPath,
            coverage: hit.coverage ?? null,
            isFloor: !!hit.isFloor,
            channels: hit.channels || null
        }))
        : items.map(item => ({
            refId: item.chunkId,
            fileId: item.documentId,
            filePath: item.sourceKey,
            title: item.sourceKey,
            content: item.text,
            score: item.score,
            scoreKind: item.scoreKind,
            source: item.connectorId,
            locator: item.locator,
            startLine: item.startLine,
            endLine: item.endLine,
            headingPath: item.headingPath,
            coverage: item.coverage ?? null,
            isFloor: !!item.isFloor,
            channels: item.channels || null
        }));

    return {
        knowledgeBase: true,
        mode,
        outcome: data.outcome || (chunks.length > 0 ? 'matched' : 'no_match'),
        completeness: data.completeness || 'complete',
        usedRecallFloor: !!data.usedRecallFloor,
        noMatch: !!data.noMatch,
        noMatchReason: data.noMatchReason || null,
        kbChannels: data.kbChannels || null,
        chunks,
        files: [],
        context: '',
        stats: {
            durationMs: Math.round(durationMs),
            estimatedTokens: 0,
            isDegraded: !!(data.degraded || data.indexDegraded),
            degradeReason: data.degradationReason || null,
            isEarlyPruned,
            timingChain: data.timingChain || null
        }
    };
}
