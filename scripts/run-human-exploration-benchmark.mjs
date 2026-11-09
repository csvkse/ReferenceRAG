/**
 * 人类探索未知知识库基准评测运行器 (Human Exploration Benchmark Runner)
 * 覆盖 7 大认知维度：
 *   1. 模糊概念探索 (VagueConceptual)
 *   2. 场景/现象描述 (ScenarioDescription)
 *   3. 同义词/词汇断层 (VocabularyMismatch)
 *   4. 口语与错别字噪声 (TypoAndNoise)
 *   5. 反事实与否定陷阱 (NegationDistractor)
 *   6. 跨章节多跳线索综合 (MultiHopSynthesis)
 *   7. 精准专名核验 (ExactVerification)
 */

import fs from 'fs';
import path from 'path';

const API_BASE = process.env.REFERENCERAG_API || 'http://127.0.0.1:5124';
const BENCHMARK_FIXTURE = path.resolve('tests/fixtures/human-exploration-benchmark.json');
const OUTPUT_RESULTS = path.resolve('docs/human-exploration-benchmark-results.json');

const Modes = {
    Lexical: { id: 'Lexical', name: '纯词法检索 (BM25+FTS5)', mode: 5 },
    Vector: { id: 'Vector', name: '稠密向量检索 (bge-m3 Dense)', mode: 0 },
    Hybrid: { id: 'Hybrid', name: '多路混合检索 (BM25+Vector+RRF)', mode: 3 }
};

async function queryApi(query, mode, topK = 5) {
    const start = performance.now();
    const res = await fetch(`${API_BASE}/api/ai/query`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            query,
            mode,
            topK,
            sources: ['jinyong_smoke', 'jinyong_core']
        })
    });
    const elapsed = Math.round(performance.now() - start);
    if (!res.ok) {
        throw new Error(`HTTP ${res.status}: ${await res.text()}`);
    }
    const data = await res.json();
    return { data, clientElapsedMs: elapsed };
}

function evaluateChunks(testItem, chunks) {
    let firstHitRank = null;
    let anyHit = false;
    let matchedKeywords = new Set();

    for (let i = 0; i < chunks.length; i++) {
        const text = (chunks[i].Content || '') + ' ' + (chunks[i].HeadingPath || '');
        const hit = testItem.mustContainAny.some(kw => text.includes(kw));
        if (hit) {
            if (firstHitRank === null) firstHitRank = i + 1;
            anyHit = true;
        }
        for (const kw of testItem.goldThemes) {
            if (text.includes(kw)) matchedKeywords.add(kw);
        }
    }

    const reciprocalRank = firstHitRank ? 1.0 / firstHitRank : 0.0;
    const coverage = testItem.goldThemes.length > 0
        ? matchedKeywords.size / testItem.goldThemes.length
        : 0;

    return {
        anyHit,
        firstHitRank,
        reciprocalRank,
        coverage
    };
}

async function main() {
    console.log(`========================================================================`);
    console.log(`🚀 启动人类探索未知知识库全维度评测基准 (Target: ${API_BASE})`);
    console.log(`========================================================================`);

    const cases = JSON.parse(fs.readFileSync(BENCHMARK_FIXTURE, 'utf8'));
    console.log(`[INFO] 载入 ${cases.length} 个覆盖 7 大认知维度的探索用例\n`);

    // Warmup
    console.log(`[Warmup] 预热各路检索通道...`);
    for (const m of Object.values(Modes)) {
        await queryApi('春秋名剑', m.mode, 3);
    }
    console.log(`[Warmup] 预热完毕，进入正式评测\n`);

    const allModeResults = {};

    for (const [modeKey, modeInfo] of Object.entries(Modes)) {
        console.log(`------------------------------------------------------------------------`);
        console.log(`▶ 评测模式: [${modeInfo.name}] (Mode=${modeInfo.mode})`);
        console.log(`------------------------------------------------------------------------`);

        const archetypeStats = {};
        const caseDetails = [];
        const latencies = [];

        for (let i = 0; i < cases.length; i++) {
            const item = cases[i];
            const arch = item.archetype;
            if (!archetypeStats[arch]) {
                archetypeStats[arch] = {
                    archetype: arch,
                    archetypeName: item.archetypeName,
                    total: 0,
                    hitAt1: 0,
                    hitAt5: 0,
                    reciprocalRanks: [],
                    coverages: [],
                    latencies: []
                };
            }

            try {
                const { data, clientElapsedMs } = await queryApi(item.question, modeInfo.mode, 5);
                latencies.push(clientElapsedMs);
                archetypeStats[arch].latencies.push(clientElapsedMs);

                const evalRes = evaluateChunks(item, (data.Chunks || []).slice(0, 5));
                archetypeStats[arch].total++;
                if (evalRes.firstHitRank === 1) archetypeStats[arch].hitAt1++;
                if (evalRes.anyHit) archetypeStats[arch].hitAt5++;
                archetypeStats[arch].reciprocalRanks.push(evalRes.reciprocalRank);
                archetypeStats[arch].coverages.push(evalRes.coverage);

                const statusTag = evalRes.anyHit ? `✔ (Rank #${evalRes.firstHitRank})` : `✖ 未召回`;
                console.log(` [${item.archetypeName}] [${item.id}] ${item.question.slice(0, 24)}... -> ${statusTag} (${clientElapsedMs}ms)`);

                caseDetails.push({
                    id: item.id,
                    archetype: item.archetype,
                    archetypeName: item.archetypeName,
                    question: item.question,
                    hit: evalRes.anyHit,
                    rank: evalRes.firstHitRank,
                    coverage: Math.round(evalRes.coverage * 100),
                    latencyMs: clientElapsedMs
                });
            } catch (err) {
                console.error(` [ERR] ${item.id} 执行失败: ${err.message}`);
            }
        }

        allModeResults[modeKey] = {
            modeKey,
            modeInfo,
            totalCases: cases.length,
            overallHitAt1: Object.values(archetypeStats).reduce((acc, a) => acc + a.hitAt1, 0),
            overallHitAt5: Object.values(archetypeStats).reduce((acc, a) => acc + a.hitAt5, 0),
            avgMrr: (Object.values(archetypeStats).flatMap(a => a.reciprocalRanks).reduce((a, b) => a + b, 0) / cases.length).toFixed(3),
            avgCoverage: (Object.values(archetypeStats).flatMap(a => a.coverages).reduce((a, b) => a + b, 0) / cases.length * 100).toFixed(1),
            avgLatency: Math.round(latencies.reduce((a, b) => a + b, 0) / latencies.length),
            archetypeBreakdown: archetypeStats,
            caseDetails
        };
        console.log(`\n模式 [${modeInfo.name}] 完成: Top-1=${allModeResults[modeKey].overallHitAt1}/${cases.length}, Top-5=${allModeResults[modeKey].overallHitAt5}/${cases.length}, 均耗=${allModeResults[modeKey].avgLatency}ms\n`);
    }

    // 格式化输出 7 大维度能力对比矩阵
    console.log(`================================================================================================`);
    console.log(`                   人类探索未知知识库 7 大认知原型评测能力矩阵 (HitRate@5)`);
    console.log(`================================================================================================`);

    const archetypesList = [
        { key: 'VagueConceptual', name: '1. 模糊概念探索' },
        { key: 'ScenarioDescription', name: '2. 场景/现象描述' },
        { key: 'VocabularyMismatch', name: '3. 同义词/词汇断层' },
        { key: 'TypoAndNoise', name: '4. 口语与错别字噪声' },
        { key: 'NegationDistractor', name: '5. 反事实与否定陷阱' },
        { key: 'MultiHopSynthesis', name: '6. 跨章节多跳线索' },
        { key: 'ExactVerification', name: '7. 精准专名核验' }
    ];

    const matrixTable = archetypesList.map(arch => {
        const row = { '认知探索维度': arch.name };
        for (const [mKey, res] of Object.entries(allModeResults)) {
            const stat = res.archetypeBreakdown[arch.key];
            if (stat && stat.total > 0) {
                const pct = Math.round((stat.hitAt5 / stat.total) * 100);
                const avgRank = stat.reciprocalRanks.filter(r => r > 0).length > 0
                    ? `(MRR ${(stat.reciprocalRanks.reduce((a, b) => a + b, 0) / stat.total).toFixed(2)})`
                    : '(0)';
                row[res.modeInfo.name] = `${pct}% ${avgRank}`;
            } else {
                row[res.modeInfo.name] = 'N/A';
            }
        }
        return row;
    });

    console.table(matrixTable);

    const overallTable = Object.values(allModeResults).map(r => ({
        检索策略: r.modeInfo.name,
        '综合 Top-1 准确率': `${Math.round((r.overallHitAt1 / r.totalCases) * 100)}% (${r.overallHitAt1}/${r.totalCases})`,
        '综合 Top-5 召回率': `${Math.round((r.overallHitAt5 / r.totalCases) * 100)}% (${r.overallHitAt5}/${r.totalCases})`,
        '平均 MRR 评分': r.avgMrr,
        '知识要素覆盖度': `${r.avgCoverage}%`,
        '端到端平均延迟': `${r.avgLatency}ms`
    }));

    console.log(`\n================================================================================================`);
    console.log(`                                综合评测总成绩与耗时一览`);
    console.log(`================================================================================================`);
    console.table(overallTable);

    fs.writeFileSync(OUTPUT_RESULTS, JSON.stringify(allModeResults, null, 2), 'utf8');
    console.log(`\n[OK] 完整探索评测明细数据已落盘至: ${OUTPUT_RESULTS}`);
}

main().catch(err => {
    console.error('[FATAL]', err);
    process.exit(1);
});
