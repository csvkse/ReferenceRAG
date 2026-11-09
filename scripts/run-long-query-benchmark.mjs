/**
 * 零向量依赖 - 纯词法长句检索专项评测基准 (Vector-Free Long Query Lexical Benchmark)
 * 验证在 30~50 字符的自然语言长句、问答套话、复杂情境描述下，
 * 纯词法检索 (Tiered NEAR + 加权 OR + PRF 伪相关反馈) 相比向量检索的准确率与毫秒级延迟表现。
 */

import fs from 'fs';
import path from 'path';

const API_BASE = process.env.REFERENCERAG_API || 'http://127.0.0.1:5124';

const TestCases = [
    {
        id: 'long-01',
        title: '口语长句与因果探究',
        query: '我想请问一下在连城诀中狄云在牢房里面被万圭陷害之后丁典传授给他的神照经救活了谁？',
        targetKws: ['神照经', '丁典', '狄云', '救活'],
        goldThemes: ['神照经', '丁典', '狄云', '自缢', '气绝']
    },
    {
        id: 'long-02',
        title: '场景细节与刀剑互换',
        query: '能否告诉我一下当年胡一刀和苗人凤在极寒之地的雪山之巅决战数日刀剑互换的惊心动魄过程？',
        targetKws: ['胡一刀', '苗人凤', '雪山', '刀剑'],
        goldThemes: ['胡一刀', '苗人凤', '刀法', '剑法', '换刀']
    },
    {
        id: 'long-03',
        title: '数字细节与越女剑法',
        query: '请问越女剑故事里面阿青用细细的竹竿单挑一千名越国甲士和一千名剑士到底是为了找谁？',
        targetKws: ['阿青', '竹棒', '越国', '西施'],
        goldThemes: ['阿青', '范蠡', '西施', '越甲', '竹棒']
    },
    {
        id: 'long-04',
        title: '无专名情境描述 (验证 PRF 涌现)',
        query: '在北方雪山顶上决斗的两位绝顶豪杰临死前把襁褓中的婴儿托付给了客栈的哪位平民仆人？',
        targetKws: ['平阿四', '婴儿', '胡斐', '雪山'],
        goldThemes: ['平阿四', '胡斐', '店家', '胡夫人']
    },
    {
        id: 'long-05',
        title: '古代鉴赏长句与典籍溯源',
        query: '我想了解关于春秋时期薛烛给越王勾践鉴定名剑时评价天下无双的纯钧剑到底有何绝妙之处？',
        targetKws: ['薛烛', '纯钧', '勾践', '赤堇'],
        goldThemes: ['薛烛', '纯钧', '越王', '欧冶子']
    },
    {
        id: 'long-06',
        title: '环境互动与雪坑反杀',
        query: '有没有记载关于血刀老祖带着水笙在雪谷中与落花流水南四奇周旋并利用雪坑反杀的精彩片段？',
        targetKws: ['血刀老祖', '水笙', '雪谷', '南四奇'],
        goldThemes: ['血刀老祖', '落花流水', '陆天抒', '雪坑']
    },
    {
        id: 'long-07',
        title: '历史前因与宏大叙事',
        query: '关于李自成大顺政权覆灭前夕留下的闯王宝藏以及四个侍卫家族世代相传的军刀与藏宝图秘密',
        targetKws: ['闯王', '李自成', '宝藏', '军刀'],
        goldThemes: ['闯王', '李自成', '苗', '范', '田', '宝刀']
    },
    {
        id: 'long-08',
        title: '跨章节千古名典因果',
        query: '请教一下范蠡带着越国西施姑娘泛舟五湖之前西施为什么会心口剧痛捧心而颦？',
        targetKws: ['西施', '范蠡', '捧心', '剑气'],
        goldThemes: ['西施', '心口', '捧心', '阿青', '竹棒']
    },
    {
        id: 'long-09',
        title: '人物命运发展与悲剧结局',
        query: '我想知道狄云从雪谷出来之后去荆州万府复仇时万圭和万震山把戚芳怎么样了？',
        targetKws: ['戚芳', '万圭', '万震山', '狄云'],
        goldThemes: ['戚芳', '万震山', '砌在墙内', '空心菜']
    },
    {
        id: 'long-10',
        title: '悬念反转与场景毒药',
        query: '在连城诀大结局里江陵城南天宁寺大铁佛背后究竟涂抹了什么导致寻宝众人疯狂惨死？',
        targetKws: ['天宁寺', '大佛', '毒', '连城诀'],
        goldThemes: ['天宁寺', '铁佛', '金猴', '剧毒', '发疯']
    }
];

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

async function runBenchmark() {
    console.log('='.repeat(80));
    console.log(`🚀 纯词法长句优化专项基准评测 (Target: ${API_BASE})`);
    console.log(`⚡ 核心约束: 100% 零向量依赖 (Zero-Vector)、纯词法近音分层与 PRF 伪相关反馈`);
    console.log('='.repeat(80));

    const modes = [
        { name: '纯词法长句检索 (BM25+NEAR+PRF)', mode: 5 },
        { name: '稠密向量检索 (bge-m3 Dense)', mode: 0 }
    ];

    const allResults = {};

    for (const m of modes) {
        console.log(`\n▶ 正在评测: [${m.name}] (Mode=${m.mode})`);
        console.log('-'.repeat(80));

        let top1Count = 0;
        let top5Count = 0;
        let totalElapsed = 0;
        const caseDetails = [];

        for (const tc of TestCases) {
            const { data, clientElapsedMs } = await queryApi(tc.query, m.mode, 5);
            totalElapsed += clientElapsedMs;

            const chunks = data.Chunks || [];
            let firstHitRank = null;

            for (let i = 0; i < chunks.length; i++) {
                const text = (chunks[i].Content || '') + ' ' + (chunks[i].HeadingPath || '');
                const hit = tc.targetKws.some(kw => text.includes(kw));
                if (hit) {
                    firstHitRank = i + 1;
                    break;
                }
            }

            if (firstHitRank === 1) top1Count++;
            if (firstHitRank !== null && firstHitRank <= 5) top5Count++;

            const status = firstHitRank === 1 ? '✔ (Rank #1)' : (firstHitRank ? `✔ (Rank #${firstHitRank})` : '✖ 未命中');
            console.log(` [${tc.id}] ${tc.title.padEnd(20)} -> ${status.padEnd(14)} (${clientElapsedMs}ms)`);
            caseDetails.push({
                id: tc.id,
                title: tc.title,
                rank: firstHitRank,
                elapsedMs: clientElapsedMs
            });
        }

        const avgElapsed = Math.round(totalElapsed / TestCases.length);
        const top1Rate = `${Math.round((top1Count / TestCases.length) * 100)}%`;
        const top5Rate = `${Math.round((top5Count / TestCases.length) * 100)}%`;

        console.log(`\n模式 [${m.name}] 评测完成: Top-1 命中率=${top1Rate} (${top1Count}/${TestCases.length}), Top-5 召回率=${top5Rate} (${top5Count}/${TestCases.length}), 平均端到端延迟=${avgElapsed}ms`);

        allResults[m.name] = {
            top1Rate,
            top1Count,
            top5Rate,
            top5Count,
            avgElapsed,
            cases: caseDetails
        };
    }

    console.log('\n' + '='.repeat(80));
    console.log('                          长句专项评测对比总表');
    console.log('='.repeat(80));
    console.table([
        {
            '检索策略': '纯词法检索 (Tiered NEAR + PRF)',
            'Top-1 准确率': allResults['纯词法长句检索 (BM25+NEAR+PRF)'].top1Rate,
            'Top-5 召回率': allResults['纯词法长句检索 (BM25+NEAR+PRF)'].top5Rate,
            '平均延迟': `${allResults['纯词法长句检索 (BM25+NEAR+PRF)'].avgElapsed}ms`,
            '零向量依赖': '100% 无向量/无GPU/零显存'
        },
        {
            '检索策略': '稠密向量检索 (bge-m3 Dense)',
            'Top-1 准确率': allResults['稠密向量检索 (bge-m3 Dense)'].top1Rate,
            'Top-5 召回率': allResults['稠密向量检索 (bge-m3 Dense)'].top5Rate,
            '平均延迟': `${allResults['稠密向量检索 (bge-m3 Dense)'].avgElapsed}ms`,
            '零向量依赖': '需要 1024-dim 嵌入计算'
        }
    ]);

    fs.writeFileSync(
        path.resolve('docs/long-query-lexical-benchmark-results.json'),
        JSON.stringify(allResults, null, 2),
        'utf8'
    );
    console.log('\n[OK] 评测数据已持久化写入 docs/long-query-lexical-benchmark-results.json');
}

runBenchmark().catch(err => {
    console.error('Benchmark failed:', err);
    process.exit(1);
});
