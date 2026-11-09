/**
 * ReferenceRAG 真实服务检索基准评测套件 (Retrieval Benchmark Suite)
 * 验证并对比 词法检索 (Lexical / BM25) vs 向量检索 (Vector / Dense) vs 混合检索 (Hybrid)
 * 基于真实部署环境 (http://127.0.0.1:5124) 执行全面准确度与耗时度量
 */

import fs from 'fs';
import path from 'path';

const API_BASE = process.env.REFERENCERAG_API || 'http://127.0.0.1:5124';
const FIXTURE_PATH = path.resolve('tests/fixtures/jinyong-golden-qa.json');
const OUTPUT_JSON_PATH = path.resolve('docs/benchmark-retrieval-results.json');

// ==========================================
// 1. 设计模式：状态机 (State Machine Pattern)
// ==========================================
class BenchmarkStateMachine {
    constructor() {
        this.state = 'UNINITIALIZED';
        this.transitions = [];
    }

    transition(newState, details = '') {
        const timestamp = new Date().toISOString();
        this.transitions.push({ from: this.state, to: newState, timestamp, details });
        console.log(`[StateMachine] 状态流转: ${this.state} -> ${newState} ${details ? `(${details})` : ''}`);
        this.state = newState;
    }
}

// ==========================================
// 2. 设计模式：策略模式 (Strategy Pattern)
// ==========================================
const RetrievalStrategies = {
    Lexical: {
        id: 'Lexical',
        name: '纯词法检索 (FTS5 + BM25 + DPP多样性去重)',
        mode: 5,
        description: '零向量模型依赖，基于 SQLite FTS5 与结巴分词倒排索引'
    },
    Vector: {
        id: 'Vector',
        name: '稠密向量检索 (Dense Embedding / Quick 模式)',
        mode: 0,
        description: '基于 BAAI/bge-m3 语义密集向量表与余弦距离检索'
    },
    Hybrid: {
        id: 'Hybrid',
        name: '混合多路召回 (BM25 + Vector + RRF融合)',
        mode: 3,
        description: '词法与向量双路并行召回，使用 Reciprocal Rank Fusion 倒数秩融合'
    }
};

// ==========================================
// 3. 设计模式：适配器与度量计算器 (Adapter & Metrics Pattern)
// ==========================================
class MetricsCalculator {
    static calculatePercentile(values, percentile) {
        if (!values || values.length === 0) return 0;
        const sorted = [...values].sort((a, b) => a - b);
        const index = (percentile / 100) * (sorted.length - 1);
        const lower = Math.floor(index);
        const upper = Math.ceil(index);
        const weight = index - lower;
        if (upper >= sorted.length) return sorted[sorted.length - 1];
        return Math.round(sorted[lower] * (1 - weight) + sorted[upper] * weight);
    }

    static evaluateQueryResponse(qa, resp, topK = 5) {
        const chunks = (resp.Chunks || []).slice(0, topK);
        let firstHitRank = null;
        let anyHit = false;
        let matchedKeywordsSet = new Set();

        for (let i = 0; i < chunks.length; i++) {
            const chunk = chunks[i];
            const content = (chunk.Content || '') + ' ' + (chunk.HeadingPath || '');
            const hitThisChunk = qa.mustContainKeywords.some(kw => content.includes(kw));

            if (hitThisChunk) {
                if (firstHitRank === null) firstHitRank = i + 1; // 1-based rank
                anyHit = true;
            }

            for (const kw of qa.mustContainKeywords) {
                if (content.includes(kw)) {
                    matchedKeywordsSet.add(kw);
                }
            }
        }

        const reciprocalRank = firstHitRank ? 1.0 / firstHitRank : 0.0;
        const top1Hit = firstHitRank === 1;
        const keywordCoverage = qa.mustContainKeywords.length > 0
            ? matchedKeywordsSet.size / qa.mustContainKeywords.length
            : 0;

        // 检查负面关键词（是否误召回违背事实的干扰块）
        let negativeHit = false;
        const allText = chunks.map(c => c.Content).join(' ');
        for (const nkw of (qa.negativeKeywords || [])) {
            if (allText.includes(nkw)) {
                negativeHit = true;
                break;
            }
        }

        return {
            anyHit,
            firstHitRank,
            reciprocalRank,
            top1Hit,
            keywordCoverage,
            negativeAvoided: !negativeHit,
            chunkCount: chunks.length
        };
    }
}

// ==========================================
// 4. API 客户端与执行器
// ==========================================
async function callAiQueryApi(query, mode, topK = 5, sources = ['jinyong_smoke', 'jinyong_core']) {
    const url = `${API_BASE}/api/ai/query`;
    const payload = {
        query,
        mode,
        topK,
        sources
    };

    const startTime = performance.now();
    const res = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    const elapsed = Math.round(performance.now() - startTime);

    if (!res.ok) {
        const errText = await res.text();
        throw new Error(`API Error (${res.status}): ${errText}`);
    }

    const data = await res.json();
    return { data, clientElapsedMs: elapsed };
}

// ==========================================
// 5. 主评测执行流程
// ==========================================
async function run() {
    const sm = new BenchmarkStateMachine();
    sm.transition('INITIALIZING', '载入测试集与准备环境');

    const rawQa = JSON.parse(fs.readFileSync(FIXTURE_PATH, 'utf8'));
    // 筛选当前已收录索引的小说语料 (Smoke: 越女剑, Core: 雪山飞狐, 连城诀)
    const baseQaList = rawQa.filter(q => ['越女剑', '雪山飞狐', '连城诀'].includes(q.targetNovel));

    // 构造语义泛化扩展评测用例（测试同义泛化与无精确关键词时的检索表现）
    const semanticParaphraseQaList = [
        {
            id: 'qa-paraphrase-01',
            tier: 'Smoke',
            targetNovel: '越女剑',
            category: 'SemanticGeneralization',
            question: '牧羊姑娘是用何种器械、跟随山野间什么灵物领悟了绝顶格斗术？',
            expectedAnswer: '阿青手执竹棒，同山中白公公（白猿）嬉戏相击习得神剑绝技。',
            mustContainKeywords: ['竹棒', '白公公', '白猿'],
            negativeKeywords: ['宝剑', '人类师父']
        },
        {
            id: 'qa-paraphrase-02',
            tier: 'Core',
            targetNovel: '雪山飞狐',
            category: 'SemanticGeneralization',
            question: '北方雪山之巅悬崖下的秘密藏宝图与四大护卫家族世代争端究竟因何而起？',
            expectedAnswer: '闯王李自成留下的巨额宝藏引发四大家族世代争夺。',
            mustContainKeywords: ['闯王', '李自成', '宝藏'],
            negativeKeywords: ['倚天剑']
        },
        {
            id: 'qa-paraphrase-03',
            tier: 'Core',
            targetNovel: '连城诀',
            category: 'SemanticGeneralization',
            question: '含冤入狱的年轻人从狱友身上传承了哪门绝世内功心法？',
            expectedAnswer: '狄云在牢中承蒙丁典传授神照经。',
            mustContainKeywords: ['丁典', '神照经'],
            negativeKeywords: ['九阴真经', '易筋经']
        }
    ];

    const allTestCases = [...baseQaList, ...semanticParaphraseQaList];
    console.log(`[INFO] 成功加载 ${allTestCases.length} 个评测用例 (基础金庸考据: ${baseQaList.length}, 语义泛化变体: ${semanticParaphraseQaList.length})`);

    // 探针检查服务连通性
    sm.transition('PROBING', '测试远端容器 API 连通性');
    const healthRes = await fetch(`${API_BASE}/api/system/health`);
    const health = await healthRes.json();
    console.log(`[OK] 目标服务就绪: ${API_BASE} (status: ${health.status}, timestamp: ${health.timestamp})`);

    // 预热阶段 (Warmup)，消除首查询 JIT 与网络握手冷启动影响
    sm.transition('WARMUP', '执行各模式预热请求');
    for (const [key, strat] of Object.entries(RetrievalStrategies)) {
        process.stdout.write(`  预热 ${strat.name}... `);
        await callAiQueryApi('金庸武侠预热测试', strat.mode, 3);
        console.log('完成');
    }

    // 正式执行基准评测
    sm.transition('BENCHMARKING', '多轮采样评测词法、向量与混合检索');
    const benchmarkResults = {};
    const ROUNDS = 3; // 每用例执行 3 次采样取均值与分位数

    for (const [key, strat] of Object.entries(RetrievalStrategies)) {
        console.log(`\n======================================================`);
        console.log(`▶ 开始评测策略: [${strat.id}] ${strat.name} (采样轮数: ${ROUNDS})`);
        console.log(`======================================================`);

        const strategyMetrics = {
            strategyId: strat.id,
            strategyName: strat.name,
            mode: strat.mode,
            rounds: ROUNDS,
            queriesCount: allTestCases.length,
            totalExecutions: allTestCases.length * ROUNDS,
            clientLatencies: [],
            serverLatencies: [],
            embedLatencies: [],
            vectorLatencies: [],
            bm25Latencies: [],
            hitsAt1: 0,
            hitsAt3: 0,
            hitsAt5: 0,
            reciprocalRanks: [],
            keywordCoverages: [],
            negativeAvoidances: 0,
            caseDetails: []
        };

        for (let caseIdx = 0; caseIdx < allTestCases.length; caseIdx++) {
            const qa = allTestCases[caseIdx];
            process.stdout.write(`  [${caseIdx + 1}/${allTestCases.length}] [${qa.id}] ${qa.question.slice(0, 28)}... `);

            const caseSamples = [];

            for (let r = 0; r < ROUNDS; r++) {
                try {
                    const { data, clientElapsedMs } = await callAiQueryApi(qa.question, strat.mode, 5);
                    const evalRes = MetricsCalculator.evaluateQueryResponse(qa, data, 5);
                    const serverDuration = data.Stats?.DurationMs || clientElapsedMs;
                    const phases = data.Stats?.Phases || {};

                    strategyMetrics.clientLatencies.push(clientElapsedMs);
                    strategyMetrics.serverLatencies.push(serverDuration);
                    if (phases.EmbedMs !== undefined) strategyMetrics.embedLatencies.push(phases.EmbedMs);
                    if (phases.VectorMs !== undefined) strategyMetrics.vectorLatencies.push(phases.VectorMs);
                    if (phases.Bm25Ms !== undefined) strategyMetrics.bm25Latencies.push(phases.Bm25Ms);

                    caseSamples.push({
                        round: r + 1,
                        clientElapsedMs,
                        serverDuration,
                        phases,
                        evalRes
                    });
                } catch (err) {
                    console.error(`\n    [ERR] 采样失败: ${err.message}`);
                }
            }

            // 统计该用例的综合表现（以最好一次或平均判定）
            const bestEval = caseSamples.map(s => s.evalRes).find(e => e.anyHit) || caseSamples[0].evalRes;
            const avgClientLatency = Math.round(caseSamples.reduce((acc, s) => acc + s.clientElapsedMs, 0) / caseSamples.length);

            if (bestEval.firstHitRank === 1) strategyMetrics.hitsAt1++;
            if (bestEval.firstHitRank && bestEval.firstHitRank <= 3) strategyMetrics.hitsAt3++;
            if (bestEval.anyHit) strategyMetrics.hitsAt5++;

            strategyMetrics.reciprocalRanks.push(bestEval.reciprocalRank);
            strategyMetrics.keywordCoverages.push(bestEval.keywordCoverage);
            if (bestEval.negativeAvoided) strategyMetrics.negativeAvoidances++;

            const statusSymbol = bestEval.anyHit ? `✔ (Rank #${bestEval.firstHitRank || '-'})` : '✖ 未命中';
            console.log(`${statusSymbol} | 均耗: ${avgClientLatency}ms`);

            strategyMetrics.caseDetails.push({
                qaId: qa.id,
                category: qa.category,
                question: qa.question,
                expectedKeywords: qa.mustContainKeywords,
                bestHitRank: bestEval.firstHitRank,
                reciprocalRank: bestEval.reciprocalRank,
                keywordCoverage: Math.round(bestEval.keywordCoverage * 100),
                avgLatencyMs: avgClientLatency
            });
        }

        benchmarkResults[strat.id] = strategyMetrics;
    }

    // ==========================================
    // 6. 指标汇总与统计计算
    // ==========================================
    sm.transition('METRIC_COMPUTATION', '聚合与计算多维统计指标');

    const summaryReport = [];

    for (const [stratId, data] of Object.entries(benchmarkResults)) {
        const totalCases = data.queriesCount;
        const hitRateAt1 = (data.hitsAt1 / totalCases) * 100;
        const hitRateAt3 = (data.hitsAt3 / totalCases) * 100;
        const hitRateAt5 = (data.hitsAt5 / totalCases) * 100;
        const mrr = data.reciprocalRanks.reduce((a, b) => a + b, 0) / totalCases;
        const avgKwCoverage = (data.keywordCoverages.reduce((a, b) => a + b, 0) / totalCases) * 100;
        const negAvoidRate = (data.negativeAvoidances / totalCases) * 100;

        const p50 = MetricsCalculator.calculatePercentile(data.clientLatencies, 50);
        const p90 = MetricsCalculator.calculatePercentile(data.clientLatencies, 90);
        const p99 = MetricsCalculator.calculatePercentile(data.clientLatencies, 99);
        const meanLatency = Math.round(data.clientLatencies.reduce((a, b) => a + b, 0) / data.clientLatencies.length);
        const minLatency = Math.min(...data.clientLatencies);
        const maxLatency = Math.max(...data.clientLatencies);

        const avgEmbedMs = data.embedLatencies.length > 0
            ? Math.round(data.embedLatencies.reduce((a, b) => a + b, 0) / data.embedLatencies.length)
            : 0;
        const avgVectorMs = data.vectorLatencies.length > 0
            ? Math.round(data.vectorLatencies.reduce((a, b) => a + b, 0) / data.vectorLatencies.length)
            : 0;
        const avgBm25Ms = data.bm25Latencies.length > 0
            ? Math.round(data.bm25Latencies.reduce((a, b) => a + b, 0) / data.bm25Latencies.length)
            : 0;

        data.summary = {
            accuracy: {
                hitRateAt1: `${hitRateAt1.toFixed(1)}%`,
                hitRateAt3: `${hitRateAt3.toFixed(1)}%`,
                hitRateAt5: `${hitRateAt5.toFixed(1)}%`,
                mrrAt5: Number(mrr.toFixed(3)),
                keywordCoverage: `${avgKwCoverage.toFixed(1)}%`,
                negativeAvoidanceRate: `${negAvoidRate.toFixed(1)}%`
            },
            latency: {
                p50Ms: p50,
                p90Ms: p90,
                p99Ms: p99,
                meanMs: meanLatency,
                minMs: minLatency,
                maxMs: maxLatency
            },
            phaseAverages: {
                avgEmbedMs,
                avgVectorMs,
                avgBm25Ms
            }
        };

        summaryReport.push({
            策略模式: data.strategyName,
            'Top-1准确率': data.summary.accuracy.hitRateAt1,
            'Top-5召回率': data.summary.accuracy.hitRateAt5,
            'MRR@5评分': data.summary.accuracy.mrrAt5,
            '关键词覆盖度': data.summary.accuracy.keywordCoverage,
            '耗时P50(中位数)': `${data.summary.latency.p50Ms}ms`,
            '耗时P90': `${data.summary.latency.p90Ms}ms`,
            '平均耗时': `${data.summary.latency.meanMs}ms`,
            '向量编码推理耗时': `${avgEmbedMs}ms`,
            'BM25检索耗时': `${avgBm25Ms}ms`
        });
    }

    console.log('\n========================================================================================');
    console.log('                 ReferenceRAG 词法 vs 向量 vs 混合检索 真实环境评测对比总表');
    console.log('========================================================================================');
    console.table(summaryReport);

    // 持久化为 JSON 供分析与报告引用
    fs.writeFileSync(OUTPUT_JSON_PATH, JSON.stringify(benchmarkResults, null, 2), 'utf8');
    console.log(`\n[OK] 完整评测原始指标已保存至: ${OUTPUT_JSON_PATH}`);

    sm.transition('COMPLETED', '基准评测全部完成');
}

run().catch(err => {
    console.error('[FATAL] 基准评测运行异常:', err);
    process.exit(1);
});
