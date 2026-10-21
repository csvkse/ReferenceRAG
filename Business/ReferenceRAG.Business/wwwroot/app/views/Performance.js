import { defineComponent as _defineComponent } from 'vue';
import { ref, h, computed, onMounted, onActivated } from 'vue';
import { NTag, NCode, NDivider, NButton, NSpace } from 'naive-ui';
import { performanceApi, semanticTestApi, rerankTestApi, modelsApi } from '/app/api/index.js';
import { ragFetch as fetch } from '/core/transport/index.js';
const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'Performance' },
    __name: 'Performance',
    setup(__props, { expose: __expose }) {
        __expose();
        const modelTelemetry = ref(null);
        const modelTelemetryLoading = ref(false);
        const loadModelTelemetry = async () => {
            modelTelemetryLoading.value = true;
            try {
                const res = await performanceApi.getModelStatus();
                modelTelemetry.value = res.data;
            }
            catch (e) {
                console.error('[ModelTelemetry]', e);
            }
            finally {
                modelTelemetryLoading.value = false;
            }
        };

        const rerankTelemetry = ref(null);
        const rerankTelemetryLoading = ref(false);
        const loadRerankTelemetry = async () => {
            rerankTelemetryLoading.value = true;
            try {
                const res = await performanceApi.getRerankStatus();
                rerankTelemetry.value = res.data;
            }
            catch (e) {
                console.error('[RerankTelemetry]', e);
            }
            finally {
                rerankTelemetryLoading.value = false;
            }
        };

        const chatTelemetry = ref(null);
        const chatTelemetryLoading = ref(false);
        const loadChatTelemetry = async () => {
            chatTelemetryLoading.value = true;
            try {
                const res = await modelsApi.getRuntimeStatus();
                chatTelemetry.value = res.data?.chat || null;
            }
            catch (e) {
                console.error('[ChatTelemetry]', e);
            }
            finally {
                chatTelemetryLoading.value = false;
            }
        };

        const loadAllTelemetry = async () => {
            await Promise.all([loadModelTelemetry(), loadRerankTelemetry(), loadChatTelemetry()]);
        };
        const getInitialModelTab = () => {
            try {
                const hash = window.location.hash || '';
                const qIndex = hash.indexOf('?');
                if (qIndex !== -1) {
                    const params = new URLSearchParams(hash.substring(qIndex + 1));
                    const t = params.get('tab');
                    if (t === 'rerank' || t === 'embedding' || t === 'chat') return t;
                }
            } catch {}
            return 'embedding';
        };
        const activeModelTab = ref(getInitialModelTab());
        const handleModelTabChange = (val) => {
            activeTelemetryTab.value = val;
            if (val === 'chat' && !chatTelemetry.value) {
                loadChatTelemetry();
            }
        };
        const activeRerankSubTab = ref('quality');
        const activeChatSubTab = ref('matrix');
        const activeTelemetryTab = ref('embedding');

        // ==================== Chat Benchmark ====================
        const chatPresetPrompts = [
            {
                label: 'RAG 原理解析',
                category: '通用问答',
                prompt: '请用简洁专业的中文，概括大语言模型（LLM）在企业级 RAG 检索增强生成架构中的核心流程与防幻觉机制。'
            },
            {
                label: 'C# 异步代码',
                category: '代码补全',
                prompt: '请用 C# 编写一段使用 Channel<T> 实现的高性能异步批量生产者-消费者流水线，并给出关键并发注释。'
            },
            {
                label: '数学与逻辑推理',
                category: '逻辑推理',
                prompt: '小明有 5 个苹果，吃了 2 个，又买了比剩下多 3 个的苹果，最后送给朋友一半，请一步步推理小明最后还剩多少个苹果？'
            },
            {
                label: '长文摘要精炼',
                category: '长文摘要',
                prompt: '请将以下文本提炼为 3 条核心要点：在微服务架构演进过程中，分布式事务一致性与数据副本同步通常采用 Saga 模式或本地消息表。前者基于事件驱动状态机，通过正向操作和补偿操作解耦业务；后者依赖本地事务与可靠消息队列保证最终一致性。在高吞吐场景下，两阶段提交（2PC）因长事务锁等待可能导致性能瓶颈。'
            }
        ];
        const chatBenchmarkPrompt = ref(chatPresetPrompts[0].prompt);
        const chatBenchmarkMaxTokens = ref(512);
        const chatBenchmarkTemperature = ref(0.7);
        const chatBenchmarkStream = ref(true);
        const chatBenchmarkLoading = ref(false);
        const chatBenchmarkStreaming = ref(false);
        const chatBenchmarkOutput = ref('');
        const chatBenchmarkMetrics = ref(null);
        const selectChatPreset = (preset) => {
            chatBenchmarkPrompt.value = preset.prompt;
        };

        const runChatBenchmark = async () => {
            if (!chatBenchmarkPrompt.value?.trim()) return;
            chatBenchmarkLoading.value = true;
            chatBenchmarkStreaming.value = true;
            chatBenchmarkOutput.value = '';
            chatBenchmarkMetrics.value = null;

            const t0 = performance.now();
            let firstTokenTime = null;
            let tokenEstimateCount = 0;

            try {
                const targetModel = chatTelemetry.value?.modelName || 'default';
                const payload = {
                    model: targetModel,
                    messages: [
                        { role: 'system', content: '你是一个严格专业的性能测速助手，回答紧凑直接。' },
                        { role: 'user', content: chatBenchmarkPrompt.value }
                    ],
                    max_tokens: chatBenchmarkMaxTokens.value,
                    temperature: chatBenchmarkTemperature.value,
                    stream: !!chatBenchmarkStream.value
                };

                const key = localStorage.getItem('reference_rag_api_key');
                const headers = { 'Content-Type': 'application/json' };
                if (key) { headers['X-API-Key'] = key; headers['Authorization'] = `Bearer ${key}`; }
                const res = await fetch('/v1/chat/completions', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify(payload)
                });

                if (!res.ok) {
                    const errText = await res.text();
                    throw new Error(`测速接口返回 HTTP ${res.status}: ${errText}`);
                }

                if (!chatBenchmarkStream.value || !res.body) {
                    const data = await res.json();
                    const text = data.choices?.[0]?.message?.content || '';
                    chatBenchmarkOutput.value = text;
                    const totalMs = Math.round(performance.now() - t0);
                    const estTokens = Math.max(1, Math.round(text.length * 0.75));
                    const tps = ((estTokens / Math.max(1, totalMs)) * 1000).toFixed(1);
                    chatBenchmarkMetrics.value = {
                        ttftMs: totalMs,
                        totalMs,
                        generationMs: totalMs,
                        tokensGenerated: estTokens,
                        tps,
                        modelUsed: targetModel
                    };
                    return;
                }

                const reader = res.body.getReader();
                const decoder = new TextDecoder();
                let buffer = '';

                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    buffer += decoder.decode(value, { stream: true });
                    const lines = buffer.split('\n');
                    buffer = lines.pop() ?? '';

                    for (const line of lines) {
                        const trimmed = line.trim();
                        if (!trimmed.startsWith('data:')) continue;
                        const dataStr = trimmed.slice(5).trim();
                        if (dataStr === '[DONE]') continue;
                        try {
                            const chunk = JSON.parse(dataStr);
                            const delta = chunk.choices?.[0]?.delta?.content || '';
                            if (delta) {
                                if (firstTokenTime === null) {
                                    firstTokenTime = Math.round(performance.now() - t0);
                                }
                                chatBenchmarkOutput.value += delta;
                                tokenEstimateCount += Math.max(1, Math.round(delta.length * 0.7));
                            }
                        } catch {
                            // ignore partial JSON chunk
                        }
                    }
                }

                const totalMs = Math.round(performance.now() - t0);
                const ttft = firstTokenTime !== null ? firstTokenTime : totalMs;
                const genMs = Math.max(1, totalMs - ttft);
                const actualTokens = Math.max(tokenEstimateCount, Math.round(chatBenchmarkOutput.value.length * 0.75));
                const tps = ((actualTokens / genMs) * 1000).toFixed(1);

                chatBenchmarkMetrics.value = {
                    ttftMs: ttft,
                    totalMs,
                    generationMs: genMs,
                    tokensGenerated: actualTokens,
                    tps,
                    modelUsed: targetModel
                };
            } catch (err) {
                console.error('[ChatBenchmark]', err);
                chatBenchmarkOutput.value = `[测速失败]: ${err.message || err}`;
            } finally {
                chatBenchmarkLoading.value = false;
                chatBenchmarkStreaming.value = false;
            }
        };

        const chatBenchmarkEvaluation = computed(() => {
            if (!chatBenchmarkMetrics.value) return null;
            const m = chatBenchmarkMetrics.value;
            const tps = parseFloat(m.tps) || 0;
            const ttft = m.ttftMs || 0;

            let grade = 'B';
            let gradeType = 'info';
            let title = '标准可用推理性能 (B 级)';

            if (tps >= 35 && ttft <= 400) {
                grade = 'S';
                gradeType = 'success';
                title = '极致极速推理性能 (S 级 - 满血 GPU 加速)';
            } else if (tps >= 15 && ttft <= 1000) {
                grade = 'A';
                gradeType = 'success';
                title = '流畅交互生产级性能 (A 级)';
            } else if (tps < 8 || ttft > 3000) {
                grade = 'C';
                gradeType = 'warning';
                title = '推理延迟较高 (C 级 - 建议优化卸载层数)';
            }

            const tips = [];
            if (ttft <= 400) {
                tips.push(`首字延迟极低 (TTFT=${ttft}ms)，用户发起提问后立即得到交互反馈，零等待感`);
            } else if (ttft <= 1200) {
                tips.push(`首字延迟处于良好区间 (TTFT=${ttft}ms)，打字机生成体感自然`);
            } else {
                tips.push(`首字延迟偏高 (${ttft}ms)，可能处于模型首次冷启动预热加载，或显存不足引起上下文交换`);
            }

            if (tps >= 25) {
                tips.push(`文本生成吞吐率高达 ${tps} Tokens/s，大幅超越正常人类阅读速度 (约 5~8 Words/s)`);
            } else if (tps >= 12) {
                tips.push(`生成速度为 ${tps} Tokens/s，满足实时问答和长文阅读要求`);
            } else {
                tips.push(`当前吞吐仅 ${tps} Tokens/s，建议在设置页面增大 GPU 卸载层数 (GpuLayers) 或换用量化规格更高的模型 (如 Q4_K_M)`);
            }

            return { grade, gradeType, title, tips, ttft, tps, totalMs: m.totalMs, tokensGenerated: m.tokensGenerated };
        });

        // ==================== Chat Evaluation Matrix & Acceptance Suite ====================
        const chatEvalCases = ref([
            {
                id: 'D1-01',
                dimension: 'D1: 确定性状态指令分发',
                title: '状态指令完全匹配',
                prompt: 'GetIndexStatus',
                expectedTool: 'GetIndexStatus',
                expectedParam: null,
                antiHallucination: false,
                description: '完全匹配工具名，必须触发 GetIndexStatus 并返回真实分块数与 BM25，拒绝 280/280 幻觉',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D1-02',
                dimension: 'D1: 确定性状态指令分发',
                title: '状态自然语言指令',
                prompt: '获取知识库当前索引状态',
                expectedTool: 'GetIndexStatus',
                expectedParam: null,
                antiHallucination: false,
                description: '中文意图指令，期望分发至 GetIndexStatus 并返回真实索引数据',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D2-01',
                dimension: 'D2: 显式搜索前缀与参数抽取',
                title: '搜索冒号前缀提取',
                prompt: '搜索: 你好',
                expectedTool: 'SearchKnowledge',
                expectedParam: '你好',
                antiHallucination: false,
                description: '解析「搜索: 你好」，期望触发 SearchKnowledge，提取 query="你好"',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D2-02',
                dimension: 'D2: 显式搜索前缀与参数抽取',
                title: '检索中文字符前缀',
                prompt: '查一下: 向量数据库',
                expectedTool: 'SearchKnowledge',
                expectedParam: '向量数据库',
                antiHallucination: false,
                description: '解析「查一下: 向量数据库」，期望触发 SearchKnowledge 检索知识库',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D3-01',
                dimension: 'D3: 复合指令前缀容错',
                title: '复合工具名前缀容错',
                prompt: 'SearchKnowledge 搜索: 你好',
                expectedTool: 'SearchKnowledge',
                expectedParam: '你好',
                antiHallucination: false,
                description: '容错剥离工具名称与冒号，期望触发 SearchKnowledge 并提取 query="你好"',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D3-02',
                dimension: 'D3: 复合指令前缀容错',
                title: '伪语法指令调用容错',
                prompt: '调用: GetIndexStatus',
                expectedTool: 'GetIndexStatus',
                expectedParam: null,
                antiHallucination: false,
                description: '剥离「调用:」伪语法前缀，期望自愈执行 GetIndexStatus',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D4-01',
                dimension: 'D4: 关键词精确检索分发',
                title: '精确关键词前缀分发',
                prompt: 'BM25: 错误码',
                expectedTool: 'BM25Search',
                expectedParam: '错误码',
                antiHallucination: false,
                description: '以 BM25: 开头，期望分发至 BM25Search 或 SearchKnowledge 检索精确关键词',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D4-02',
                dimension: 'D4: 关键词精确检索分发',
                title: '复合 BM25 工具前缀',
                prompt: 'BM25Search 搜索: 数据库连接超时',
                expectedTool: 'BM25Search',
                expectedParam: '数据库连接超时',
                antiHallucination: false,
                description: '复合前缀自愈，期望分发至 BM25Search 精确搜索数据库连接超时',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D5-01',
                dimension: 'D5: 幻觉抵抗与空召回保真',
                title: '空召回保真与防脑补',
                prompt: '搜索: 不存在的绝密量子笔记XYZ',
                expectedTool: 'SearchKnowledge',
                expectedParam: '不存在的绝密量子笔记XYZ',
                antiHallucination: true,
                description: '搜索不存在的笔记，模型应据实回复未找到或调用搜索，严禁凭空捏造笔记内容',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D6-01',
                dimension: 'D6: 正常对话不误判',
                title: '正常身份闲聊拒调',
                prompt: '你好，请用一句话介绍一下你自己',
                expectedTool: 'none',
                expectedParam: null,
                antiHallucination: false,
                description: '日常问好与自我介绍，必须正常回复，严禁误触发任何检索工具',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D6-02',
                dimension: 'D6: 正常对话不误判',
                title: '纯理论知识答疑拒调',
                prompt: '请简要解释什么是向量检索？',
                expectedTool: 'none',
                expectedParam: null,
                antiHallucination: false,
                description: '通用技术原理解析，直接作答，严禁误触发知识库检索工具',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            },
            {
                id: 'D7-01',
                dimension: 'D7: 格式自愈与宽容解析',
                title: 'JSON 语法与伪调用自愈',
                prompt: 'tool_call: GetIndexStatus',
                expectedTool: 'GetIndexStatus',
                expectedParam: null,
                antiHallucination: false,
                description: '针对小模型常生成的伪 tool_call 文本，系统能宽容识别并执行对应工具',
                status: 'idle',
                resultText: '',
                executedTools: [],
                latencyMs: 0,
                passed: null,
                reason: ''
            }
        ]);
        chatEvalCases.value.forEach(c => {
            c.ttftMs = null;
            c.tps = null;
            c.tokensGenerated = 0;
        });

        const chatEvalRunning = ref(false);
        const chatEvalProgress = ref({ current: 0, total: 12, percent: 0, currentCase: '' });
        let chatEvalAbortController = null;

        const evaluateChatResult = (item, responseText) => {
            const text = responseText || '';
            const badgeRegex = /> 🔧 \*\*已执行工具\*\*:\s*([^\n]+)/i;
            const match = text.match(badgeRegex);
            const executedTools = [];
            if (match && match[1]) {
                const tools = match[1].split(',').map(s => s.replace(/[`\s]/g, '')).filter(Boolean);
                executedTools.push(...tools);
            }
            item.executedTools = executedTools;

            // D6: 正常对话不误判
            if (item.expectedTool === 'none') {
                if (executedTools.length > 0) {
                    return {
                        passed: false,
                        reason: '未遵循拒调约束：闲聊指令误触发了工具 [' + executedTools.join(', ') + ']'
                    };
                }
                if (text.length < 5) {
                    return {
                        passed: false,
                        reason: '模型未给出有效文本回答'
                    };
                }
                return {
                    passed: true,
                    reason: '成功拒调工具，正常输出自然助手文本'
                };
            }

            // D5: 幻觉抵抗与空召回保真
            if (item.antiHallucination) {
                const forbiddenHallucinations = ['找到了相关笔记', '关于绝密量子笔记的内容如下', '绝密量子笔记XYZ包含以下内容'];
                const hasHallucination = forbiddenHallucinations.some(term => text.includes(term));
                if (hasHallucination) {
                    return {
                        passed: false,
                        reason: '检测到严重幻觉：模型在空召回情况下虚构了笔记正文'
                    };
                }
                const mentionsNotFound = text.includes('未找到') || text.includes('没有找到') || text.includes('未检索到') || text.includes('暂无') || text.includes('不存在');
                if (executedTools.includes('SearchKnowledge') || executedTools.includes('BM25Search') || mentionsNotFound) {
                    return {
                        passed: true,
                        reason: '成功执行检索并如实声明未找到，有效抵御幻觉'
                    };
                }
                return {
                    passed: true,
                    reason: '未凭空捏造虚假笔记，回答符合事实'
                };
            }

            // 针对特定工具判定 (D1, D2, D3, D4, D7)
            const targetTool = item.expectedTool;
            const matchedTool = executedTools.find(t =>
                t.toLowerCase() === targetTool.toLowerCase() ||
                (targetTool === 'BM25Search' && (t.toLowerCase() === 'bm25search' || t.toLowerCase() === 'searchknowledge'))
            );

            if (!matchedTool) {
                if (targetTool === 'GetIndexStatus' && (text.includes('分块') || text.includes('BM25') || text.includes('索引') || text.includes('向量'))) {
                    if (/(?:280\s*\/\s*280|280\s*个?\s*BM25|BM25[^\d]{0,8}280\s*个?(?:文档)?)/i.test(text)) {
                        return {
                            passed: false,
                            reason: '检测到旧版本 280/280 伪造幻觉数据'
                        };
                    }
                    return {
                        passed: true,
                        reason: '返回了真实索引状态或触发了对应工具'
                    };
                }
                return {
                    passed: false,
                    reason: '未触发预期工具 [' + targetTool + ']，实际执行: [' + (executedTools.length ? executedTools.join(', ') : '无') + ']'
                };
            }

            if (targetTool === 'GetIndexStatus') {
                if (/(?:280\s*\/\s*280|280\s*个?\s*BM25|BM25[^\d]{0,8}280\s*个?(?:文档)?)/i.test(text)) {
                    return {
                        passed: false,
                        reason: '执行了工具但回答捏造了 280/280 幻觉数据'
                    };
                }
            }

            return {
                passed: true,
                reason: '成功分发并触发预期工具 [' + matchedTool + ']'
            };
        };

        const runSingleChatEvalCase = async (item) => {
            item.status = 'running';
            item.passed = null;
            item.reason = '';
            item.resultText = '';
            item.executedTools = [];
            item.ttftMs = null;
            item.tps = null;
            item.tokensGenerated = 0;

            const t0 = performance.now();
            try {
                const targetModel = chatTelemetry.value?.modelName || 'default';
                const payload = {
                    model: targetModel,
                    messages: [
                        { role: 'user', content: item.prompt }
                    ],
                    max_tokens: 300,
                    temperature: 0.1,
                    stream: true
                };

                const key = localStorage.getItem('reference_rag_api_key');
                const headers = { 'Content-Type': 'application/json' };
                if (key) { headers['X-API-Key'] = key; headers['Authorization'] = 'Bearer ' + key; }

                const res = await fetch('/v1/chat/completions', {
                    method: 'POST',
                    headers,
                    body: JSON.stringify(payload)
                });

                if (!res.ok) {
                    const errText = await res.text();
                    throw new Error('HTTP ' + res.status + ': ' + errText);
                }

                let content = '';
                let firstTokenTime = null;
                let tokenCount = 0;

                const contentType = res.headers.get('content-type') || '';
                if (res.body && (contentType.includes('text/event-stream') || contentType.includes('stream'))) {
                    const reader = res.body.getReader();
                    const decoder = new TextDecoder();
                    let buffer = '';

                    while (true) {
                        const { done, value } = await reader.read();
                        if (done) break;
                        buffer += decoder.decode(value, { stream: true });
                        const lines = buffer.split('\n');
                        buffer = lines.pop() ?? '';

                        for (const line of lines) {
                            const trimmed = line.trim();
                            if (!trimmed.startsWith('data:')) continue;
                            const dataStr = trimmed.slice(5).trim();
                            if (dataStr === '[DONE]') continue;
                            try {
                                const chunk = JSON.parse(dataStr);
                                const delta = chunk.choices?.[0]?.delta?.content || '';
                                if (delta) {
                                    if (firstTokenTime === null) {
                                        firstTokenTime = Math.round(performance.now() - t0);
                                    }
                                    content += delta;
                                    tokenCount += Math.max(1, Math.round(delta.length * 0.7));
                                }
                                if (chunk.usage?.completion_tokens) {
                                    tokenCount = chunk.usage.completion_tokens;
                                }
                            } catch {
                                // partial chunk
                            }
                        }
                    }
                } else {
                    const data = await res.json();
                    content = data.choices?.[0]?.message?.content || '';
                    if (data.usage?.completion_tokens) {
                        tokenCount = data.usage.completion_tokens;
                    }
                }

                const totalMs = Math.round(performance.now() - t0);
                const ttft = firstTokenTime !== null ? firstTokenTime : totalMs;
                const genMs = Math.max(1, totalMs - ttft);
                const actualTokens = Math.max(tokenCount, Math.round(content.length * 0.75));
                const tps = genMs > 0 ? ((actualTokens / genMs) * 1000).toFixed(1) : '0.0';

                item.resultText = content;
                item.latencyMs = totalMs;
                item.ttftMs = ttft;
                item.tps = tps;
                item.tokensGenerated = actualTokens;

                const evalRes = evaluateChatResult(item, content);
                item.passed = evalRes.passed;
                item.reason = evalRes.reason;
                item.status = item.passed ? 'success' : 'error';
            } catch (err) {
                item.latencyMs = Math.round(performance.now() - t0);
                item.status = 'error';
                item.passed = false;
                item.reason = '调用异常: ' + (err.message || err);
                item.resultText = '[错误]: ' + (err.message || err);
            }
        };

        const runAllChatEvalCases = async () => {
            if (chatEvalRunning.value) return;
            chatEvalRunning.value = true;
            chatEvalAbortController = new AbortController();

            const total = chatEvalCases.value.length;
            chatEvalProgress.value = { current: 0, total, percent: 0, currentCase: '' };

            for (let i = 0; i < total; i++) {
                if (chatEvalAbortController.signal.aborted) break;
                const c = chatEvalCases.value[i];
                chatEvalProgress.value.current = i + 1;
                chatEvalProgress.value.percent = Math.round(((i + 1) / total) * 100);
                chatEvalProgress.value.currentCase = '[' + c.id + '] ' + c.title;
                await runSingleChatEvalCase(c);
            }

            chatEvalRunning.value = false;
        };

        const cancelChatEval = () => {
            if (chatEvalAbortController) {
                chatEvalAbortController.abort();
            }
            chatEvalRunning.value = false;
        };

        const chatEvalStats = computed(() => {
            const cases = chatEvalCases.value;
            const total = cases.length;
            const tested = cases.filter(c => c.passed !== null);
            const passed = cases.filter(c => c.passed === true);
            const failed = cases.filter(c => c.passed === false);
            const passRate = tested.length > 0 ? ((passed.length / tested.length) * 100).toFixed(1) : '0';
            const avgLatency = tested.length > 0
                ? Math.round(tested.reduce((sum, c) => sum + (c.latencyMs || 0), 0) / tested.length)
                : 0;
            const testedWithTtft = tested.filter(c => c.ttftMs !== null && c.ttftMs !== undefined);
            const avgTtft = testedWithTtft.length > 0
                ? Math.round(testedWithTtft.reduce((sum, c) => sum + c.ttftMs, 0) / testedWithTtft.length)
                : 0;
            const testedWithTps = tested.filter(c => c.tps !== null && !isNaN(parseFloat(c.tps)) && parseFloat(c.tps) > 0);
            const avgTps = testedWithTps.length > 0
                ? (testedWithTps.reduce((sum, c) => sum + parseFloat(c.tps), 0) / testedWithTps.length).toFixed(1)
                : '0.0';
            const totalTokens = tested.reduce((sum, c) => sum + (c.tokensGenerated || 0), 0);

            const dimMap = {};
            for (const c of cases) {
                if (!dimMap[c.dimension]) {
                    dimMap[c.dimension] = { dimension: c.dimension, total: 0, tested: 0, passed: 0, failed: 0 };
                }
                dimMap[c.dimension].total++;
                if (c.passed !== null) {
                    dimMap[c.dimension].tested++;
                    if (c.passed) dimMap[c.dimension].passed++;
                    else dimMap[c.dimension].failed++;
                }
            }
            const dimensions = Object.values(dimMap).map(d => ({
                ...d,
                passRate: d.tested > 0 ? ((d.passed / d.tested) * 100).toFixed(0) : '0'
            }));

            let grade = 'B';
            let gradeType = 'info';
            let title = '可用级工具调用与问答能力 (B 级)';

            const rateNum = parseFloat(passRate);
            if (rateNum >= 95) {
                grade = 'S';
                gradeType = 'success';
                title = '卓越高可靠工具调用与抗幻觉能力 (S 级)';
            } else if (rateNum >= 80) {
                grade = 'A';
                gradeType = 'success';
                title = '生产就绪级工具交互与自愈能力 (A 级)';
            } else if (tested.length > 0 && rateNum < 70) {
                grade = 'C';
                gradeType = 'warning';
                title = '存在显著漏调或幻觉风险 (C 级 - 建议优化 Prompt 或微调)';
            }

            return {
                total,
                testedCount: tested.length,
                passedCount: passed.length,
                failedCount: failed.length,
                passRate,
                avgLatency,
                avgTtft,
                avgTps,
                totalTokens,
                dimensions,
                grade,
                gradeType,
                title
            };
        });

        const exportChatEvalReport = () => {
            const stats = chatEvalStats.value;
            const model = chatTelemetry.value?.modelName || 'default';
            const date = new Date().toLocaleString();
            let md = '# ReferenceRAG 聊天模型工具调用与质量评测报告\n\n';
            md += '- **评测模型**: ' + model + '\n';
            md += '- **后端格式**: ' + (chatTelemetry.value?.modelFormat || 'gguf') + '\n';
            md += '- **硬件加速**: ' + (chatTelemetry.value?.isGpuMode ? 'GPU 硬件加速' : 'CPU 计算模式') + '\n';
            md += '- **评测时间**: ' + date + '\n';
            md += '- **评测综合得分**: ' + stats.passRate + '% (' + stats.grade + ' 级 - ' + stats.title + ')\n';
            md += '- **平均单例耗时**: ' + stats.avgLatency + ' ms\n';
            md += '- **平均首字延迟 (TTFT)**: ' + stats.avgTtft + ' ms\n';
            md += '- **平均生成吞吐 (TPS)**: ' + stats.avgTps + ' tok/s\n';
            md += '- **总生成 Token 数**: ' + stats.totalTokens + ' tokens\n\n';

            md += '## 一、能力维度统计\n\n';
            md += '| 维度 | 总用例数 | 已测用例 | 通过用例 | 通过率 |\n';
            md += '| :--- | :--- | :--- | :--- | :--- |\n';
            for (const d of stats.dimensions) {
                md += '| ' + d.dimension + ' | ' + d.total + ' | ' + d.tested + ' | ' + d.passed + ' | ' + d.passRate + '% |\n';
            }
            md += '\n';

            md += '## 二、测试用例明细表\n\n';
            md += '| 用例编号 | 维度 | 标题 | 输入 Prompt | 期望工具 | 实际工具 | 首字 (TTFT) | 吞吐 (TPS) | 总耗时 | 状态 | 判定原因 |\n';
            md += '| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n';
            for (const c of chatEvalCases.value) {
                const exec = c.executedTools && c.executedTools.length ? c.executedTools.join(', ') : '无';
                const st = c.passed === true ? '✅ 通过' : (c.passed === false ? '❌ 未通过' : '⚪ 未测试');
                const ttftStr = c.ttftMs != null ? c.ttftMs + 'ms' : '-';
                const tpsStr = c.tps ? c.tps + ' tok/s' : '-';
                md += '| ' + c.id + ' | ' + c.dimension + ' | ' + c.title + ' | `' + c.prompt + '` | `' + c.expectedTool + '` | `' + exec + '` | ' + ttftStr + ' | ' + tpsStr + ' | ' + c.latencyMs + 'ms | ' + st + ' | ' + (c.reason || '-') + ' |\n';
            }
            md += '\n';

            md += '## 三、模型调优与优化建议\n\n';
            if (stats.failedCount === 0) {
                md += '> ✅ **全项绿灯**：当前聊天模型与工具调用调度层配合优异，指令分发、参数提取、空召回抗幻觉及拒调约束均达到 S/A 级水准，完全满足生产部署要求。\n';
            } else {
                md += '> ⚠️ **发现 ' + stats.failedCount + ' 项未通过用例**：\n';
                for (const c of chatEvalCases.value.filter(x => x.passed === false)) {
                    md += '- **[' + c.id + '] ' + c.title + '**: 提示词 `' + c.prompt + '` 判定失败：' + c.reason + '。\n';
                }
                md += '\n**工程建议**：\n';
                md += '1. **小模型 (<1B) Prompt 微调建议**：在 System Prompt 中加入更强烈的引导模式，例如显式规范 `遇到以「搜索:」开头的问题，强制调用 SearchKnowledge`；\n';
                md += '2. **量化精度影响**：若使用 Q4 量化出现逻辑衰退，推荐升级至 Q5_K_M 或 Q8_0 以保持注意力精度；\n';
                md += '3. **确定性兜底分发**：本系统已内置双层自愈分发网关，保证核心指令即使在小模型概率漂移时仍能稳定触发工具。\n';
            }

            const blob = new Blob([md], { type: 'text/markdown;charset=utf-8;' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'chat-model-eval-report-' + model.replace(/[/\\:]/g, '_') + '-' + Date.now() + '.md';
            a.click();
            URL.revokeObjectURL(url);
        };

        const exportChatEvalCasesJson = () => {
            const exportData = {
                version: '1.0.0',
                exportDate: new Date().toISOString(),
                model: chatTelemetry.value?.modelName || 'default',
                cases: chatEvalCases.value.map(c => ({
                    id: c.id,
                    dimension: c.dimension,
                    title: c.title,
                    prompt: c.prompt,
                    expectedTool: c.expectedTool,
                    expectedParam: c.expectedParam || null,
                    antiHallucination: !!c.antiHallucination,
                    description: c.description,
                    lastRun: {
                        passed: c.passed,
                        executedTools: c.executedTools,
                        latencyMs: c.latencyMs,
                        ttftMs: c.ttftMs,
                        tps: c.tps,
                        tokensGenerated: c.tokensGenerated,
                        reason: c.reason,
                        resultSnippet: c.resultText ? c.resultText.slice(0, 300) : null
                    }
                }))
            };
            const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: 'application/json;charset=utf-8;' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'chat-evaluation-cases-' + Date.now() + '.json';
            a.click();
            URL.revokeObjectURL(url);
        };

        const chatEvalColumns = [
            {
                title: '用例编号 / 场景',
                key: 'id',
                width: 170,
                render: (row) => {
                    return h(NSpace, { vertical: true, size: 2 }, {
                        default: () => [
                            h(NTag, { size: 'small', type: 'info', bordered: false, round: true }, { default: () => row.id }),
                            h('span', { style: 'font-weight: 600; font-size: 13px; margin-top: 2px' }, row.title)
                        ]
                    });
                }
            },
            {
                title: '评测维度',
                key: 'dimension',
                width: 170,
                render: (row) => {
                    return h(NTag, { size: 'small', bordered: false }, { default: () => row.dimension });
                }
            },
            {
                title: '测试输入 (Prompt)',
                key: 'prompt',
                width: 220,
                render: (row) => {
                    return h('code', {
                        style: 'background: rgba(128,128,128,0.12); padding: 2px 6px; border-radius: 4px; font-family: monospace; font-size: 12px; word-break: break-all;'
                    }, row.prompt);
                }
            },
            {
                title: '期望行为 / 工具',
                key: 'expectedTool',
                width: 150,
                render: (row) => {
                    if (row.expectedTool === 'none') {
                        return h(NTag, { size: 'small', type: 'default', round: true }, { default: () => '普通回复 (拒调)' });
                    }
                    return h(NSpace, { vertical: true, size: 2 }, {
                        default: () => [
                            h(NTag, { size: 'small', type: 'primary', round: true }, { default: () => '🔧 ' + row.expectedTool }),
                            row.expectedParam ? h('span', { style: 'font-size: 11px; color: #888' }, '参数: ' + row.expectedParam) : null
                        ]
                    });
                }
            },
            {
                title: '实际调用工具',
                key: 'executedTools',
                width: 150,
                render: (row) => {
                    if (!row.executedTools || row.executedTools.length === 0) {
                        return row.status === 'idle' ? h('span', { style: 'color: #999' }, '—') : h(NTag, { size: 'small', type: 'default', bordered: false }, { default: () => '无工具调用' });
                    }
                    return h(NSpace, { size: 4 }, {
                        default: () => row.executedTools.map(t => h(NTag, { size: 'small', type: 'success', round: true }, { default: () => '✓ ' + t }))
                    });
                }
            },
            {
                title: '测试状态',
                key: 'status',
                width: 110,
                render: (row) => {
                    if (row.status === 'running') {
                        return h(NTag, { size: 'small', type: 'warning', round: true }, { default: () => '⚡ 评测中' });
                    }
                    if (row.passed === true) {
                        return h(NTag, { size: 'small', type: 'success', round: true }, { default: () => '✅ 通过' });
                    }
                    if (row.passed === false) {
                        return h(NTag, { size: 'small', type: 'error', round: true }, { default: () => '❌ 未通过' });
                    }
                    return h(NTag, { size: 'small', type: 'default', round: true }, { default: () => '⚪ 待测试' });
                }
            },
            {
                title: '首字 / 吞吐',
                key: 'telemetry',
                width: 140,
                render: (row) => {
                    if (row.latencyMs > 0) {
                        return h(NSpace, { vertical: true, size: 2 }, {
                            default: () => [
                                h('span', { style: 'font-size: 11px; color: #888' }, '首字: ' + (row.ttftMs != null ? row.ttftMs + 'ms' : '—')),
                                h('span', { style: 'font-size: 11px; font-weight: 600; color: var(--n-value-text-color)' }, '吞吐: ' + (row.tps ? row.tps + ' t/s' : '—'))
                            ]
                        });
                    }
                    return '—';
                }
            },
            {
                title: '总耗时',
                key: 'latencyMs',
                width: 90,
                render: (row) => {
                    if (row.latencyMs > 0) {
                        return row.latencyMs + ' ms';
                    }
                    return '—';
                }
            },
            {
                title: '判定说明 / 回显摘要',
                key: 'reason',
                ellipsis: { tooltip: true },
                render: (row) => {
                    if (row.reason) {
                        return h('span', { style: { color: row.passed ? 'inherit' : 'var(--n-error-color)' } }, row.reason);
                    }
                    return row.description;
                }
            },
            {
                title: '操作',
                key: 'actions',
                width: 90,
                render: (row) => {
                    return h(NButton, {
                        size: 'tiny',
                        secondary: true,
                        type: 'primary',
                        loading: row.status === 'running',
                        disabled: chatEvalRunning.value,
                        onClick: () => runSingleChatEvalCase(row)
                    }, { default: () => (row.passed !== null ? '重测' : '测试') });
                }
            }
        ];

        const chatEvalDimensionColumns = [
            { title: '能力维度', key: 'dimension' },
            { title: '总用例数', key: 'total', width: 90 },
            { title: '已测数', key: 'tested', width: 90 },
            { title: '通过数', key: 'passed', width: 90 },
            {
                title: '通过率',
                key: 'passRate',
                width: 120,
                render: (row) => {
                    const rate = parseInt(row.passRate) || 0;
                    return h(NTag, {
                        size: 'small',
                        type: rate >= 80 ? 'success' : (rate >= 50 ? 'warning' : 'error'),
                        round: true
                    }, { default: () => row.passRate + '%' });
                }
            }
        ];

        const probe = ref(null);
        const probeLoading = ref(false);
        const runProbe = async () => {
            probeLoading.value = true;
            try {
                const res = await semanticTestApi.modelProbe();
                probe.value = res.data;
            }
            catch (e) {
                console.error('[ModelProbe]', e);
            }
            finally {
                probeLoading.value = false;
            }
        };
        onMounted(() => {
            loadModelTelemetry();
            loadRerankTelemetry();
            loadChatTelemetry();
            loadRerankPresets();
            loadRetrievalPresets();
        });
        onActivated(() => {
            loadModelTelemetry();
            loadRerankTelemetry();
            loadChatTelemetry();
        });
        // ==================== Semantic Test ====================
        const semanticLoading = ref(false);
        const customSemanticLoading = ref(false);
        const showCustomSemantic = ref(false);
        const semanticResult = ref(null);
        const activeTestCategory = ref(null);
        const useRemoteForSemantic = ref(false);
        const customSemantic = ref({ text1: '', text2: '', expected: 80 });
        const customSemanticResult = ref(null);
        // ==================== 语义测试数据集（参考 EmbeddingQueryTest.cs）====================
        const categoryPassCriteria = {
            '同义词': (a) => a >= 0.5,
            '同义词(中英)': (a) => a >= 0.5,
            '同义表达': (a) => a >= 0.5,
            '口语化': (a) => a >= 0.5,
            '反义词': (a) => a <= 0.55,
            '情感反义': (a) => a <= 0.55,
            '状态反义': (a) => a <= 0.55,
            '同类竞品': (a) => a >= 0.3 && a <= 0.8,
            '同类技术': (a) => a >= 0.3 && a <= 0.8,
            '同类信息': (a) => a >= 0.3 && a <= 0.8,
            '无关主题': (a) => a <= 0.4,
            '完全无关': (a) => a <= 0.15,
            '上下位关系': (a, e) => Math.abs(a - e) <= e * 0.35,
            '同类并列': (a, e) => Math.abs(a - e) <= e * 0.35,
            '因果关系': (a, e) => Math.abs(a - e) <= e * 0.35,
            '情感递进': (a, e) => Math.abs(a - e) <= e * 0.35,
            '程度递进': (a, e) => Math.abs(a - e) <= e * 0.35,
            '情感强度': (a, e) => Math.abs(a - e) <= e * 0.35,
            '多义词-水果/公司': (a, e) => Math.abs(a - e) <= e * 0.35,
            '多义词-金融机构/河岸': (a, e) => Math.abs(a - e) <= e * 0.35,
            '多义词-植物/花费': (a, e) => Math.abs(a - e) <= e * 0.35,
            '反问→陈述': (a, e) => Math.abs(a - e) <= e * 0.35,
            '双重否定→肯定': (a, e) => Math.abs(a - e) <= e * 0.35,
            '否定→双重否定': (a, e) => Math.abs(a - e) <= e * 0.35,
            '非对称问答': (a) => a >= 0.55,
            '条件互斥': (a) => a <= 0.50,
            '否定限定': (a) => a <= 0.50,
            '跨语言-中英同义': (a, e) => Math.abs(a - e) <= e * 0.35,
            '跨语言-术语对齐': (a, e) => Math.abs(a - e) <= e * 0.35,
            '负面程度差异': (a, e) => Math.abs(a - e) <= e * 0.35,
            '负面情绪强度': (a, e) => Math.abs(a - e) <= e * 0.35,
            // 小数与数字处理
            '精确数字匹配': (a) => a >= 0.85,
            '近似数字': (a, e) => Math.abs(a - e) <= e * 0.35,
            '同类型数字': (a, e) => Math.abs(a - e) <= e * 0.35,
            '不同领域数字': (a) => a <= 0.35,
            '数学常数': (a) => a >= 0.80,
            '分数表示': (a) => a >= 0.80,
            // 特殊符号处理
            '邮箱格式': (a) => a >= 0.85,
            '电话格式': (a) => a >= 0.85,
            'URL格式': (a) => a >= 0.85,
            '数学符号': (a, e) => Math.abs(a - e) <= e * 0.35,
            '货币符号': (a, e) => Math.abs(a - e) <= e * 0.35,
            '编程符号': (a, e) => Math.abs(a - e) <= e * 0.35,
            // 特殊格式处理
            '日期格式': (a) => a >= 0.85,
            '时间格式': (a) => a >= 0.80,
            '文件路径': (a, e) => Math.abs(a - e) <= e * 0.35,
            'JSON格式': (a, e) => Math.abs(a - e) <= e * 0.35,
            '时间戳': (a, e) => Math.abs(a - e) <= e * 0.35,
            '版本号': (a, e) => Math.abs(a - e) <= e * 0.35,
            // 非中英文文本
            '日文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '韩文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '俄文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '阿拉伯文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '泰文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '法文翻译': (a, e) => Math.abs(a - e) <= e * 0.35,
            '日文语义': (a, e) => Math.abs(a - e) <= e * 0.35,
        };
        const defaultPassCriteria = (actual, expected) => Math.abs(actual - expected) <= expected * 0.35;
        const evaluatePassByCategory = (category, actual, expected) => {
            return (categoryPassCriteria[category] || defaultPassCriteria)(actual, expected);
        };
        // 完整语义测试数据集（100 个测试用例：64 中文 + 36 英文，22 分类）
        const semanticCategories = [
            {
                label: '同义词与同义表达',
                description: '测试模型识别同义表达的能力，期望高相似度',
                cases: [
                    { query: '我喜欢吃苹果', text: '我爱吃苹果', expectedSimilarity: 0.85, category: '同义词' },
                    { query: '这个产品很好用', text: '这个产品非常好用', expectedSimilarity: 0.85, category: '同义词' },
                    { query: '机器学习是人工智能的分支', text: 'ML是AI的一个子领域', expectedSimilarity: 0.75, category: '同义词(中英)' },
                    { query: '今天天气很热', text: '今天气温很高', expectedSimilarity: 0.80, category: '同义表达' },
                    { query: '购买商品', text: '买东西', expectedSimilarity: 0.75, category: '口语化' },
                ]
            },
            {
                label: '反义词与对立语义',
                description: '测试模型区分反义关系的能力，期望低相似度',
                cases: [
                    { query: '这个产品很好用', text: '这个产品很难用', expectedSimilarity: 0.20, category: '反义词' },
                    { query: '今天天气很热', text: '今天天气很冷', expectedSimilarity: 0.15, category: '反义词' },
                    { query: '价格上涨了', text: '价格下跌了', expectedSimilarity: 0.20, category: '反义词' },
                    { query: '我喜欢这部电影', text: '我讨厌这部电影', expectedSimilarity: 0.15, category: '情感反义' },
                    { query: '项目成功了', text: '项目失败了', expectedSimilarity: 0.20, category: '状态反义' },
                ]
            },
            {
                label: '相关但不同主题',
                description: '测试模型识别相关但不同领域内容的粒度区分能力',
                cases: [
                    { query: '苹果公司发布了新产品', text: '三星电子推出新款手机', expectedSimilarity: 0.50, category: '同类竞品' },
                    { query: 'Python编程入门', text: 'Java编程教程', expectedSimilarity: 0.55, category: '同类技术' },
                    { query: '北京天气预报', text: '上海天气预报', expectedSimilarity: 0.60, category: '同类信息' },
                    { query: 'Python是一门编程语言', text: 'Java广泛用于企业开发', expectedSimilarity: 0.55, category: '同类并列' },
                ]
            },
            {
                label: '无关主题',
                description: '测试模型识别完全不相关内容的能力，期望极低相似度',
                cases: [
                    { query: '如何做红烧肉', text: 'Python编程入门', expectedSimilarity: 0.10, category: '无关主题' },
                    { query: '股票投资策略', text: '今天吃什么', expectedSimilarity: 0.10, category: '无关主题' },
                    { query: '足球比赛结果', text: '机器学习算法', expectedSimilarity: 0.10, category: '无关主题' },
                    { query: '如何制作蛋糕', text: '火箭发射原理', expectedSimilarity: 0.05, category: '完全无关' },
                    { query: '股票投资技巧', text: '猫咪饲养指南', expectedSimilarity: 0.05, category: '完全无关' },
                ]
            },
            {
                label: '上下位关系',
                description: '测试模型理解概念层级（上位词/下位词）的能力',
                cases: [
                    { query: '金毛犬是很聪明的宠物', text: '狗是人类的好朋友', expectedSimilarity: 0.65, category: '上下位关系' },
                    { query: '苹果富含维生素C', text: '水果对健康有益', expectedSimilarity: 0.70, category: '上下位关系' },
                ]
            },
            {
                label: '因果关系',
                description: '测试模型识别因果语义关联的能力',
                cases: [
                    { query: '昨晚下了一整夜大雨', text: '今天路面湿滑难行', expectedSimilarity: 0.60, category: '因果关系' },
                    { query: '他每天坚持锻炼一小时', text: '他的身体素质明显提升', expectedSimilarity: 0.55, category: '因果关系' },
                    { query: '项目需求频繁变更', text: '开发进度严重滞后', expectedSimilarity: 0.50, category: '因果关系' },
                ]
            },
            {
                label: '情感递进与程度',
                description: '测试模型区分情感/程度强度差异的敏感度',
                cases: [
                    { query: '我喜欢这本书', text: '我非常热爱这本书', expectedSimilarity: 0.80, category: '情感递进' },
                    { query: '这个项目有一定难度', text: '这个项目极具挑战性', expectedSimilarity: 0.75, category: '程度递进' },
                    { query: '他对服务很满意', text: '他对服务极其满意', expectedSimilarity: 0.75, category: '情感强度' },
                    { query: '这个产品有点小问题', text: '这个产品存在严重缺陷', expectedSimilarity: 0.60, category: '负面程度差异' },
                    { query: '我对他的行为有点不满', text: '我对他的行为非常愤怒', expectedSimilarity: 0.55, category: '负面情绪强度' },
                ]
            },
            {
                label: '多义词歧义',
                description: '测试模型在多义词上下文中的消歧能力',
                cases: [
                    { query: '我买了一斤苹果', text: '苹果公司发布了新手机', expectedSimilarity: 0.25, category: '多义词-水果/公司' },
                    { query: '他在银行工作', text: '河边的银行很滑', expectedSimilarity: 0.20, category: '多义词-金融机构/河岸' },
                    { query: '这朵花很美', text: '他花了很多钱', expectedSimilarity: 0.15, category: '多义词-植物/花费' },
                ]
            },
            {
                label: '复杂句式转换',
                description: '测试模型理解反问、双重否定等复杂句式的能力',
                cases: [
                    { query: '难道你不觉得这部电影很好看吗？', text: '这部电影确实非常好看', expectedSimilarity: 0.85, category: '反问→陈述' },
                    { query: '我不得不承认他的做法是对的', text: '我承认他的做法是正确的', expectedSimilarity: 0.80, category: '双重否定→肯定' },
                    { query: '这个问题很难解决', text: '没有什么问题是解决不了的', expectedSimilarity: 0.65, category: '否定→双重否定' },
                ]
            },
            {
                label: '非对称长短问答',
                description: '测试模型在短提问与长篇解答段落之间的语义对齐能力，检验非对称编码质量',
                cases: [
                    { query: '如何解决 Docker 容器内部无法解析外网域名的问题？', text: '在 Linux 宿主机上排查 /etc/resolv.conf 配置，或在 /etc/docker/daemon.json 中显式添加 dns 服务器配置并重启 Docker 守护进程，即可解决容器内部 DNS 解析超时。', expectedSimilarity: 0.85, category: '非对称问答' },
                    { query: 'Redis 缓存穿透的根本原因与解决方案', text: '缓存穿透是指查询一个根本不存在的数据，缓存与数据库均未命中从而直接穿透到底层数据库。工业级解法包括采用布隆过滤器（Bloom Filter）在网关层前置拦截，或对空结果进行短期缓存。', expectedSimilarity: 0.85, category: '非对称问答' },
                    { query: '什么是微服务架构中的断路器模式？', text: '断路器模式（Circuit Breaker）用于防止分布式系统级联故障。当依赖的下游服务连续调用超时或异常达到阈值时，断路器自动打开并快速熔断降级，待经过恢复时间窗口后进入半开状态尝试探测恢复。', expectedSimilarity: 0.85, category: '非对称问答' },
                    { query: '如何排查 .NET 生产环境 CPU 飙高至 100%？', text: '在 Linux 生产环境中可使用 dotnet-dump collect 获取进程快照，通过 dotnet-dump analyze 配合 clrthreads、threadpool 和 clrstack 命令定位死循环或高并发自旋等待的热点线程堆栈。', expectedSimilarity: 0.80, category: '非对称问答' },
                ]
            },
            {
                label: '条件互斥与否定限定',
                description: '测试模型识别否定排他限制与逻辑条件互斥的能力，期望较低相似度',
                cases: [
                    { query: '寻找支持异步非阻塞但不支持事务特性的键值数据库', text: '该数据库原生具备完整的 ACID 强事务与两阶段提交机制，适合金融级账本场景。', expectedSimilarity: 0.25, category: '条件互斥' },
                    { query: '不要推荐带有图形界面的软件，仅需纯命令行工具', text: '这是一款拥有现代化美观图形界面（GUI）的桌面应用程序，支持鼠标拖拽操作。', expectedSimilarity: 0.20, category: '否定限定' },
                    { query: '仅限周末或节假日使用的特惠门票', text: '此门票仅限工作日周一至周五入园，法定节假日及周末不可使用。', expectedSimilarity: 0.15, category: '条件互斥' },
                    { query: '不包含任何含糖添加剂的天然纯牛奶', text: '本产品含有高浓度蔗糖与果葡糖浆，口感清甜浓郁。', expectedSimilarity: 0.15, category: '否定限定' },
                ]
            },
            {
                label: '跨语言语义对齐',
                description: '测试模型跨语言语义理解能力（中文↔英文）',
                cases: [
                    { query: '我爱你', text: 'I love you', expectedSimilarity: 0.90, category: '跨语言-中英同义' },
                    { query: '今天天气很好', text: 'Today is a beautiful day', expectedSimilarity: 0.85, category: '跨语言-中英同义' },
                    { query: '机器学习', text: 'Machine Learning', expectedSimilarity: 0.88, category: '跨语言-术语对齐' },
                ]
            },
            {
                label: '小数与数字处理',
                description: '测试模型对小数、数字的语义理解能力',
                cases: [
                    { query: '商品价格是99.99元', text: '该商品售价99.99元', expectedSimilarity: 0.95, category: '精确数字匹配' },
                    { query: '商品价格是99.99元', text: '价格约为100元', expectedSimilarity: 0.70, category: '近似数字' },
                    { query: '商品价格是99.99元', text: '商品价格是49.50元', expectedSimilarity: 0.65, category: '同类型数字' },
                    { query: '商品价格是99.99元', text: '温度为36.5摄氏度', expectedSimilarity: 0.20, category: '不同领域数字' },
                    { query: '圆周率约等于3.14159', text: 'Pi的值约为3.14159', expectedSimilarity: 0.92, category: '数学常数' },
                    { query: '成绩得分88.5分', text: '考试分数为88.5', expectedSimilarity: 0.90, category: '分数表示' },
                ]
            },
            {
                label: '特殊符号处理',
                description: '测试模型对邮箱、电话、URL等特殊符号格式的理解',
                cases: [
                    { query: 'Email: test@example.com', text: '邮箱地址是test@example.com', expectedSimilarity: 0.95, category: '邮箱格式' },
                    { query: '电话: +86-138-1234-5678', text: '联系电话+86-138-1234-5678', expectedSimilarity: 0.95, category: '电话格式' },
                    { query: '网址: https://example.com', text: 'URL地址为https://example.com', expectedSimilarity: 0.95, category: 'URL格式' },
                    { query: '代码: x + y = z', text: '数学表达式x加y等于z', expectedSimilarity: 0.75, category: '数学符号' },
                    { query: '价格: $99.99', text: '售价为99.99美元', expectedSimilarity: 0.85, category: '货币符号' },
                    { query: '表达式: a && b || c', text: '逻辑与或表达式', expectedSimilarity: 0.60, category: '编程符号' },
                ]
            },
            {
                label: '特殊格式处理',
                description: '测试模型对日期、时间、文件路径等特殊格式的理解',
                cases: [
                    { query: '日期: 2024-01-15', text: '2024年1月15日', expectedSimilarity: 0.95, category: '日期格式' },
                    { query: '时间: 14:30:00', text: '下午2点30分', expectedSimilarity: 0.90, category: '时间格式' },
                    { query: '文件: C:\\Users\\file.txt', text: 'Windows文件路径', expectedSimilarity: 0.70, category: '文件路径' },
                    { query: 'JSON: {"name": "test"}', text: 'JSON数据格式name为test', expectedSimilarity: 0.80, category: 'JSON格式' },
                    { query: '时间戳: 1705312200', text: 'Unix时间戳格式', expectedSimilarity: 0.60, category: '时间戳' },
                    { query: '版本: v1.2.3-beta', text: '版本号1.2.3测试版', expectedSimilarity: 0.85, category: '版本号' },
                ]
            },
            {
                label: '非中英文文本',
                description: '测试模型对日文、韩文、俄文、阿拉伯文等非中英文的理解',
                cases: [
                    { query: 'こんにちは世界', text: '世界你好', expectedSimilarity: 0.85, category: '日文翻译' },
                    { query: '안녕하세요', text: '你好', expectedSimilarity: 0.80, category: '韩文翻译' },
                    { query: 'Привет мир', text: '世界你好', expectedSimilarity: 0.75, category: '俄文翻译' },
                    { query: 'مرحبا بالعالم', text: '世界你好', expectedSimilarity: 0.70, category: '阿拉伯文翻译' },
                    { query: 'สวัสดีโลก', text: '世界你好', expectedSimilarity: 0.70, category: '泰文翻译' },
                    { query: 'Bonjour le monde', text: '世界你好', expectedSimilarity: 0.80, category: '法文翻译' },
                    { query: '日本語で挨拶します', text: '用日语打招呼', expectedSimilarity: 0.88, category: '日文语义' },
                ]
            },
            // ========== 英文测试分类（用于测试英文模型如 all-MiniLM-L6-v2）==========
            {
                label: '英文同义词与同义表达',
                description: 'English synonyms and paraphrases - test synonym recognition',
                cases: [
                    { query: 'I enjoy eating apples', text: 'I love eating apples', expectedSimilarity: 0.85, category: 'EN-Synonym' },
                    { query: 'This product works great', text: 'This product is very useful', expectedSimilarity: 0.85, category: 'EN-Synonym' },
                    { query: 'The weather is hot today', text: 'It is very warm today', expectedSimilarity: 0.80, category: 'EN-Paraphrase' },
                    { query: 'Machine learning is a branch of AI', text: 'ML is a subfield of artificial intelligence', expectedSimilarity: 0.85, category: 'EN-Tech synonym' },
                    { query: 'How do I learn programming?', text: 'What is the best way to study coding?', expectedSimilarity: 0.80, category: 'EN-Question paraphrase' },
                ]
            },
            {
                label: '英文反义词与对立语义',
                description: 'English antonyms and opposing meanings - test contrast detection',
                cases: [
                    { query: 'This product is excellent', text: 'This product is terrible', expectedSimilarity: 0.15, category: 'EN-Antonym' },
                    { query: 'The weather is hot today', text: 'The weather is cold today', expectedSimilarity: 0.15, category: 'EN-Antonym' },
                    { query: 'Prices increased significantly', text: 'Prices dropped sharply', expectedSimilarity: 0.20, category: 'EN-Opposite' },
                    { query: 'I love this movie', text: 'I hate this movie', expectedSimilarity: 0.15, category: 'EN-Sentiment opposite' },
                    { query: 'The project succeeded', text: 'The project failed', expectedSimilarity: 0.20, category: 'EN-Status opposite' },
                ]
            },
            {
                label: '英文无关主题',
                description: 'English unrelated topics - test ability to identify irrelevant content',
                cases: [
                    { query: 'How to cook steak', text: 'Python programming tutorial', expectedSimilarity: 0.10, category: 'EN-Unrelated' },
                    { query: 'Stock investment strategies', text: 'What to eat for dinner', expectedSimilarity: 0.10, category: 'EN-Unrelated' },
                    { query: 'Football match results', text: 'Machine learning algorithms', expectedSimilarity: 0.10, category: 'EN-Unrelated' },
                    { query: 'How to bake a cake', text: 'Rocket propulsion principles', expectedSimilarity: 0.05, category: 'EN-Completely unrelated' },
                    { query: 'Investment tips for stocks', text: 'Cat care guide', expectedSimilarity: 0.05, category: 'EN-Completely unrelated' },
                ]
            },
            {
                label: '英文相关但不同主题',
                description: 'English related but different topics - test fine-grained distinction',
                cases: [
                    { query: 'Apple released a new product', text: 'Samsung launched a new phone', expectedSimilarity: 0.50, category: 'EN-Competing products' },
                    { query: 'Python programming basics', text: 'Java programming tutorial', expectedSimilarity: 0.55, category: 'EN-Similar tech' },
                    { query: 'New York weather forecast', text: 'Los Angeles weather forecast', expectedSimilarity: 0.60, category: 'EN-Same info type' },
                    { query: 'Python is a programming language', text: 'Java is widely used in enterprise', expectedSimilarity: 0.55, category: 'EN-Related tech' },
                ]
            },
            {
                label: '英文因果关系',
                description: 'English causal relationships - test cause-effect understanding',
                cases: [
                    { query: 'It rained heavily all night', text: 'The roads are slippery today', expectedSimilarity: 0.60, category: 'EN-Causal' },
                    { query: 'He exercises for an hour every day', text: 'His physical fitness has improved significantly', expectedSimilarity: 0.55, category: 'EN-Causal' },
                    { query: 'Project requirements change frequently', text: 'Development progress is seriously delayed', expectedSimilarity: 0.50, category: 'EN-Causal' },
                ]
            },
            {
                label: '英文情感与程度',
                description: 'English sentiment and degree expressions - test intensity detection',
                cases: [
                    { query: 'I like this book', text: 'I absolutely love this book', expectedSimilarity: 0.80, category: 'EN-Sentiment intensity' },
                    { query: 'This project is somewhat difficult', text: 'This project is extremely challenging', expectedSimilarity: 0.75, category: 'EN-Degree' },
                    { query: 'He is satisfied with the service', text: 'He is extremely satisfied with the service', expectedSimilarity: 0.75, category: 'EN-Intensity' },
                    { query: 'This product has minor issues', text: 'This product has serious defects', expectedSimilarity: 0.60, category: 'EN-Negative degree' },
                ]
            },
            {
                label: '英文多义词歧义',
                description: 'English polysemy disambiguation - test context understanding',
                cases: [
                    { query: 'I went to the bank to deposit money', text: 'The river bank was muddy', expectedSimilarity: 0.20, category: 'EN-Polysemy-bank' },
                    { query: 'The bat flew through the cave', text: 'He hit the ball with a bat', expectedSimilarity: 0.15, category: 'EN-Polysemy-bat' },
                    { query: 'She parked the car on the street', text: 'The park is beautiful in autumn', expectedSimilarity: 0.15, category: 'EN-Polysemy-park' },
                ]
            }
        ];
        const semanticTotalCount = computed(() => semanticCategories.reduce((s, c) => s + c.cases.length, 0));
        const semanticProgress = ref({ current: 0, total: 108, percent: 0, currentCategory: '' });
        // 运行全部测试
        const runSemanticTest = async () => {
            semanticLoading.value = true;
            semanticResult.value = null;
            activeTestCategory.value = null;
            let totalCaseCount = 0;
            for (const cat of semanticCategories) {
                totalCaseCount += cat.cases.length;
            }
            semanticProgress.value = { current: 0, total: totalCaseCount, percent: 0, currentCategory: '' };
            try {
                if (useRemoteForSemantic.value && remoteApi.value.baseUrl) {
                    const results = await runSemanticTestWithRemote(semanticCategories);
                    semanticResult.value = results;
                }
                else {
                    const allCandidates = [];
                    let totalExpected = 0;
                    let totalActual = 0;
                    let totalExpectedSq = 0;
                    let totalActualSq = 0;
                    let totalProduct = 0;
                    let totalDeviation = 0;
                    let totalDeviationSq = 0;
                    let processedCount = 0;

                    for (const category of semanticCategories) {
                        if (category.cases.length === 0)
                            continue;
                        semanticProgress.value.currentCategory = category.label;
                        for (const c of category.cases) {
                            try {
                                const res = await semanticTestApi.shortText({
                                    query: c.query,
                                    candidates: [{ text: c.text, expectedSimilarity: c.expectedSimilarity }]
                                });
                                if (res.data?.candidates?.[0]) {
                                    const candidate = res.data.candidates[0];
                                    candidate._category = c.category;
                                    allCandidates.push(candidate);

                                    const expected = c.expectedSimilarity;
                                    const actual = candidate.similarity ?? 0;
                                    const deviation = candidate.deviation ?? 0;
                                    totalExpected += expected;
                                    totalActual += actual;
                                    totalExpectedSq += expected * expected;
                                    totalActualSq += actual * actual;
                                    totalProduct += expected * actual;
                                    totalDeviation += deviation;
                                    totalDeviationSq += deviation * deviation;
                                }
                            }
                            catch (caseErr) {
                                console.warn('[SemanticTest] 单用例执行失败:', c.query, caseErr);
                            }
                            finally {
                                processedCount++;
                                semanticProgress.value.current = processedCount;
                                semanticProgress.value.percent = Math.round((processedCount / totalCaseCount) * 100);
                            }
                        }
                    }

                    const n = allCandidates.length;
                    if (n > 0) {
                        const meanExpected = totalExpected / n;
                        const meanActual = totalActual / n;
                        const covariance = totalProduct / n - meanExpected * meanActual;
                        const stdExpected = Math.sqrt(totalExpectedSq / n - meanExpected * meanExpected);
                        const stdActual = Math.sqrt(totalActualSq / n - meanActual * meanActual);
                        const correlation = stdExpected * stdActual > 0 ? covariance / (stdExpected * stdActual) : 0;
                        semanticResult.value = {
                            testType: 'comprehensive',
                            modelName: probe.value?.modelName ?? 'unknown',
                            timestamp: new Date().toISOString(),
                            candidates: allCandidates,
                            meanAbsoluteError: totalDeviation / n,
                            rootMeanSquareError: Math.sqrt(totalDeviationSq / n),
                            correlationCoefficient: correlation,
                            rankingAccuracy: 0,
                        };
                    }
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                semanticLoading.value = false;
            }
        };
        // 使用远程 API 运行语义测试
        const runSemanticTestWithRemote = async (categories) => {
            const startTime = performance.now();
            const allCandidates = [];
            let totalExpected = 0;
            let totalActual = 0;
            let totalExpectedSq = 0;
            let totalActualSq = 0;
            let totalProduct = 0;
            let totalDeviation = 0;
            let totalDeviationSq = 0;
            for (const category of categories) {
                if (category.cases.length === 0)
                    continue;
                // 批量发送所有文本对
                const allTexts = [];
                for (const c of category.cases) {
                    allTexts.push(c.query, c.text);
                }
                const embeddings = await callEmbeddingApi(allTexts);
                for (let i = 0; i < category.cases.length; i++) {
                    const c = category.cases[i];
                    const actual = cosineSimilarity(embeddings[i * 2], embeddings[i * 2 + 1]);
                    const expected = c.expectedSimilarity;
                    const deviation = Math.abs(actual - expected);
                    allCandidates.push({
                        text: c.text,
                        similarity: actual,
                        expectedSimilarity: expected,
                        deviation,
                        tokenCount: estimateTokens(c.text)
                    });
                    allCandidates[allCandidates.length - 1]._category = c.category;
                    // 统计计算
                    totalExpected += expected;
                    totalActual += actual;
                    totalExpectedSq += expected * expected;
                    totalActualSq += actual * actual;
                    totalProduct += expected * actual;
                    totalDeviation += deviation;
                    totalDeviationSq += deviation * deviation;
                }
            }
            const n = allCandidates.length;
            const meanExpected = totalExpected / n;
            const meanActual = totalActual / n;
            // Pearson 相关系数
            const covariance = totalProduct / n - meanExpected * meanActual;
            const stdExpected = Math.sqrt(totalExpectedSq / n - meanExpected * meanExpected);
            const stdActual = Math.sqrt(totalActualSq / n - meanActual * meanActual);
            const correlation = (stdExpected * stdActual) > 0 ? covariance / (stdExpected * stdActual) : 0;
            return {
                testType: 'comprehensive-remote',
                modelName: remoteApi.value.model || 'remote',
                timestamp: new Date().toISOString(),
                totalEmbeddingMs: performance.now() - startTime,
                candidates: allCandidates,
                meanAbsoluteError: totalDeviation / n,
                rootMeanSquareError: Math.sqrt(totalDeviationSq / n),
                correlationCoefficient: correlation
            };
        };
        // 按分类运行测试
        const runCategoryTest = async (category) => {
            semanticLoading.value = true;
            semanticResult.value = null;
            activeTestCategory.value = category.label;
            try {
                if (useRemoteForSemantic.value && remoteApi.value.baseUrl) {
                    // 使用远程 API
                    const result = await runSemanticTestWithRemote([category]);
                    semanticResult.value = result;
                }
                else {
                    // 使用本地 API - 修复：每个 case 单独测试其 query-text pair
                    const allCandidates = [];
                    let totalExpected = 0;
                    let totalActual = 0;
                    let totalExpectedSq = 0;
                    let totalActualSq = 0;
                    let totalProduct = 0;
                    let totalDeviation = 0;
                    let totalDeviationSq = 0;
                    for (const c of category.cases) {
                        const res = await semanticTestApi.shortText({
                            query: c.query,
                            candidates: [{ text: c.text, expectedSimilarity: c.expectedSimilarity }]
                        });
                        if (res.data.candidates && res.data.candidates[0]) {
                            const candidate = res.data.candidates[0];
                            candidate._category = c.category;
                            allCandidates.push(candidate);
                            const expected = c.expectedSimilarity;
                            const actual = candidate.similarity ?? 0;
                            const deviation = candidate.deviation ?? 0;
                            totalExpected += expected;
                            totalActual += actual;
                            totalExpectedSq += expected * expected;
                            totalActualSq += actual * actual;
                            totalProduct += expected * actual;
                            totalDeviation += deviation;
                            totalDeviationSq += deviation * deviation;
                        }
                    }
                    const n = allCandidates.length;
                    const meanExpected = totalExpected / n;
                    const meanActual = totalActual / n;
                    const covariance = totalProduct / n - meanExpected * meanActual;
                    const stdExpected = Math.sqrt(totalExpectedSq / n - meanExpected * meanExpected);
                    const stdActual = Math.sqrt(totalActualSq / n - meanActual * meanActual);
                    const correlation = stdExpected * stdActual > 0 ? covariance / (stdExpected * stdActual) : 0;
                    semanticResult.value = {
                        testType: 'category',
                        modelName: 'bge-small-zh-v1.5',
                        timestamp: new Date().toISOString(),
                        candidates: allCandidates,
                        meanAbsoluteError: totalDeviation / n,
                        rootMeanSquareError: Math.sqrt(totalDeviationSq / n),
                        correlationCoefficient: correlation,
                        rankingAccuracy: 0,
                    };
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                semanticLoading.value = false;
            }
        };
        const runCustomSemantic = async () => {
            customSemanticLoading.value = true;
            customSemanticResult.value = null;
            try {
                if (useRemoteForSemantic.value && remoteApi.value.baseUrl) {
                    // 使用远程 API
                    const embeddings = await callEmbeddingApi([customSemantic.value.text1, customSemantic.value.text2]);
                    const actual = cosineSimilarity(embeddings[0], embeddings[1]);
                    const expected = customSemantic.value.expected / 100;
                    customSemanticResult.value = {
                        testType: 'custom-remote',
                        modelName: remoteApi.value.model || 'remote',
                        timestamp: new Date().toISOString(),
                        candidates: [{
                                text: customSemantic.value.text2,
                                similarity: actual,
                                expectedSimilarity: expected,
                                deviation: Math.abs(actual - expected),
                                tokenCount: estimateTokens(customSemantic.value.text2)
                            }]
                    };
                }
                else {
                    const res = await semanticTestApi.shortText({
                        query: customSemantic.value.text1,
                        candidates: [{ text: customSemantic.value.text2, expectedSimilarity: customSemantic.value.expected / 100 }]
                    });
                    customSemanticResult.value = res.data;
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                customSemanticLoading.value = false;
            }
        };
        // ==================== 嵌入模型四大能力域画像 ====================
        const semanticDomainDefs = [
            {
                key: 'retrieval',
                name: 'RAG 核心检索',
                icon: '🎯',
                weight: 0.45,
                tagType: 'success',
                description: '同义表达、跨语言语义映射与长短问答匹配（RAG 生产环境召回核心）',
                categories: ['同义词', '同义词(中英)', '同义表达', '口语化', '非对称问答', '跨语言-中英同义', '跨语言-术语对齐', 'EN-Synonym', 'EN-Paraphrase', 'EN-Tech synonym', 'EN-Question paraphrase', '日文翻译', '韩文翻译', '俄文翻译', '阿拉伯文翻译', '泰文翻译', '法文翻译', '日文语义']
            },
            {
                key: 'discrimination',
                name: '语义边界辨析',
                icon: '🔍',
                weight: 0.25,
                tagType: 'info',
                description: '概念层级（上下位）、同类技术/竞品区分、因果关联与完全无关噪音过滤',
                categories: ['同类竞品', '同类技术', '同类信息', '同类并列', '上下位关系', '因果关系', '情感递进', '程度递进', '情感强度', '负面程度差异', '负面情绪强度', '无关主题', '完全无关', 'EN-Related', 'EN-Irrelevant']
            },
            {
                key: 'logic_adversarial',
                name: '逻辑对抗与排他',
                icon: '⚖️',
                weight: 0.15,
                tagType: 'warning',
                description: '反义词极性对立、条件互斥与否定排他（双塔模型天花板，采用边际区分度考核）',
                categories: ['反义词', '情感反义', '状态反义', '条件互斥', '否定限定', '多义词-水果/公司', '多义词-金融机构/河岸', '多义词-植物/花费', '反问→陈述', '双重否定→肯定', '否定→双重否定', 'EN-Antonym', 'EN-Opposite', 'EN-Sentiment opposite', 'EN-Status opposite']
            },
            {
                key: 'format_entity',
                name: '格式与实体兼容',
                icon: '🔢',
                weight: 0.15,
                tagType: 'default',
                description: '精确数值、日期时间戳、URL/邮箱与代码符号等特殊格式的编码鲁棒性',
                categories: ['精确数字匹配', '近似数字', '同类型数字', '不同领域数字', '数学常数', '分数表示', '邮箱格式', '电话格式', 'URL格式', '数学符号', '货币符号', '编程符号', '日期格式', '时间格式', '文件路径', 'JSON格式', '时间戳', '版本号']
            }
        ];

        const semanticDomainStats = computed(() => {
            if (!semanticResult.value?.candidates || semanticResult.value.candidates.length === 0) return [];
            const candidates = semanticResult.value.candidates;
            return semanticDomainDefs.map(domain => {
                const domainCases = candidates.filter(c => domain.categories.includes(c._category));
                const total = domainCases.length;
                if (total === 0) {
                    return { ...domain, total: 0, passed: 0, passRate: 0, avgDeviation: '0.000', rating: '未评测' };
                }
                let passed = 0;
                let totalDev = 0;
                for (const c of domainCases) {
                    const exp = c.expectedSimilarity || 0;
                    const act = c.similarity || 0;
                    if (evaluatePassByCategory(c._category, act, exp)) {
                        passed++;
                    }
                    totalDev += Math.abs(act - exp);
                }
                const passRate = Math.round((passed / total) * 100);
                const rating = passRate >= 85 ? '卓越' : passRate >= 70 ? '良好' : passRate >= 50 ? '合格' : '需优化';
                return {
                    ...domain,
                    total,
                    passed,
                    passRate,
                    avgDeviation: (totalDev / total).toFixed(3),
                    rating
                };
            });
        });

        const ragWeightedScore = computed(() => {
            const stats = semanticDomainStats.value;
            if (stats.length === 0) return '0';
            let weightedSum = 0;
            let weightTotal = 0;
            for (const s of stats) {
                if (s.total > 0) {
                    weightedSum += s.passRate * s.weight;
                    weightTotal += s.weight;
                }
            }
            return weightTotal > 0 ? (weightedSum / weightTotal).toFixed(1) : '0';
        });

        // ==================== 重排模型四大精排能力域画像 ====================
        const rerankDomainDefs = [
            {
                key: 'adversarial',
                name: '硬负例与数值约束对抗力',
                icon: '🛡️',
                weight: 0.25,
                tagType: 'warning',
                description: '甄别高词频重合但逻辑相反、极性对立或数值硬约束越界的强干扰文档',
                presetNames: ['antonym-polarity-rerank', 'hard-negatives-rerank', 'numerical-entity-rerank']
            },
            {
                key: 'long_context',
                name: '长文本深层理解',
                icon: '📜',
                weight: 0.25,
                tagType: 'info',
                description: '穿透长段落规章、制度与云原生文档，精准定位深层核心条款',
                presetNames: ['long-context-rerank', 'document-retrieval']
            },
            {
                key: 'code_troubleshooting',
                name: '代码、时效与技术排错',
                icon: '💻',
                weight: 0.30,
                tagType: 'success',
                description: '代码实现精准检索、框架版本演进时效废弃识别与死锁根因定位',
                presetNames: ['code-rerank', 'code-search', 'tech-troubleshooting-rerank', 'version-temporal-rerank']
            },
            {
                key: 'multilingual_faq',
                name: '跨语言与业务 FAQ',
                icon: '🌐',
                weight: 0.20,
                tagType: 'default',
                description: '跨境支付多语言问答与电商规则 FAQ 排序稳定性',
                presetNames: ['cross-lingual-rerank', 'enterprise-faq-rerank', 'multilingual-query', 'faq-matching', 'mixed-rerank', 'chinese-rerank', 'english-rerank']
            }
        ];

        const rerankDomainStats = computed(() => {
            if (!rerankAllPresetsResults.value || rerankAllPresetsResults.value.length === 0) return [];
            const results = rerankAllPresetsResults.value;
            return rerankDomainDefs.map(domain => {
                const matches = results.filter(r => domain.presetNames.includes(r.name));
                if (matches.length === 0) {
                    return { ...domain, count: 0, avgNdcg: '0.000', avgMrr: '0.000', avgAccuracy: '0.000', avgLatency: 0, score: '0.0', rating: '未评测' };
                }
                const count = matches.length;
                const avgNdcg = matches.reduce((acc, cur) => acc + (cur.ndcg || 0), 0) / count;
                const avgMrr = matches.reduce((acc, cur) => acc + (cur.mrr || 0), 0) / count;
                const avgAccuracy = matches.reduce((acc, cur) => acc + (cur.rankingAccuracy || 0), 0) / count;
                const avgLatency = Math.round(matches.reduce((acc, cur) => acc + (cur.queryMs || 0), 0) / count);
                const score = (avgNdcg * 0.45 + avgAccuracy * 0.35 + avgMrr * 0.20) * 100;
                const rating = score >= 85 ? '卓越' : score >= 70 ? '良好' : score >= 50 ? '合格' : '需优化';
                return {
                    ...domain,
                    count,
                    avgNdcg: avgNdcg.toFixed(3),
                    avgMrr: avgMrr.toFixed(3),
                    avgAccuracy: avgAccuracy.toFixed(3),
                    avgLatency,
                    score: score.toFixed(1),
                    rating
                };
            });
        });

        const rerankCompositeIndex = computed(() => {
            const stats = rerankDomainStats.value;
            if (stats.length === 0) return '0';
            let weightedSum = 0;
            let weightTotal = 0;
            for (const s of stats) {
                if (s.count > 0) {
                    weightedSum += parseFloat(s.score) * s.weight;
                    weightTotal += s.weight;
                }
            }
            return weightTotal > 0 ? (weightedSum / weightTotal).toFixed(1) : '0';
        });

        const getSimilarityType = (actual, expected) => {
            const deviation = Math.abs(actual - expected);
            if (deviation <= 0.15)
                return 'success';
            if (deviation <= 0.3)
                return 'warning';
            return 'error';
        };
        const categoryStats = computed(() => {
            if (!semanticResult.value?.candidates)
                return [];
            const catMap = new Map();
            for (const c of semanticResult.value.candidates) {
                const cat = c._category;
                if (!cat)
                    continue;
                const expected = c.expectedSimilarity || 0;
                const actual = c.similarity || 0;
                const deviation = Math.abs(actual - expected);
                const passed = evaluatePassByCategory(cat, actual, expected);
                const stat = catMap.get(cat) || { label: cat, total: 0, passed: 0, totalDeviation: 0 };
                stat.total++;
                if (passed)
                    stat.passed++;
                stat.totalDeviation += deviation;
                catMap.set(cat, stat);
            }
            return Array.from(catMap.entries()).map(([category, s]) => ({
                category,
                label: s.label,
                total: s.total,
                passed: s.passed,
                passRate: `${(s.passed / s.total * 100).toFixed(0)}%`,
                avgDeviation: (s.totalDeviation / s.total).toFixed(3)
            }));
        });
        const overallPassRate = computed(() => {
            if (!semanticResult.value?.candidates)
                return { passed: 0, total: 0, rate: '0%' };
            let passed = 0;
            let total = 0;
            for (const c of semanticResult.value.candidates) {
                const cat = c._category;
                if (!cat)
                    continue;
                total++;
                if (evaluatePassByCategory(cat, c.similarity || 0, c.expectedSimilarity || 0))
                    passed++;
            }
            return { passed, total, rate: `${(passed / total * 100).toFixed(0)}%` };
        });
        const categoryColumns = [
            { title: '类别', key: 'category', width: 140, ellipsis: { tooltip: true } },
            { title: '总数', key: 'total', width: 60 },
            { title: '通过', key: 'passed', width: 60 },
            { title: '通过率', key: 'passRate', width: 80, render: (row) => h(NTag, {
                    type: parseInt(row.passRate) >= 80 ? 'success' : parseInt(row.passRate) >= 50 ? 'warning' : 'error',
                    size: 'small'
                }, { default: () => row.passRate }) },
            { title: '平均偏差', key: 'avgDeviation', width: 90 },
        ];
        const semanticColumns = [
            { title: '文本', key: 'text', ellipsis: { tooltip: true }, width: 200 },
            {
                title: '类别', key: '_category', width: 140,
                render: (row) => {
                    const cat = row._category || '';
                    return h(NTag, { size: 'small', type: 'info' }, { default: () => cat });
                }
            },
            { title: '期望', key: 'expectedSimilarity', width: 70, render: (row) => `${((row.expectedSimilarity || 0) * 100).toFixed(0)}%` },
            { title: '实际', key: 'similarity', width: 70, render: (row) => `${((row.similarity || 0) * 100).toFixed(1)}%` },
            {
                title: '偏差', key: 'deviation', width: 70,
                render: (row) => {
                    const dev = row.deviation ?? 0;
                    return h(NTag, {
                        type: dev <= 0.15 ? 'success' : dev <= 0.3 ? 'warning' : 'error',
                        size: 'small'
                    }, { default: () => dev.toFixed(3) });
                }
            },
            { title: 'Token', key: 'tokenCount', width: 60 },
            {
                title: '判定', key: '_passed', width: 60,
                render: (row) => {
                    const cat = row._category;
                    const actual = row.similarity || 0;
                    const expected = row.expectedSimilarity || 0;
                    const passed = cat ? evaluatePassByCategory(cat, actual, expected) : false;
                    return h(NTag, { type: passed ? 'success' : 'error', size: 'small' }, {
                        default: () => passed ? '通过' : '未通过'
                    });
                }
            },
        ];
        // ==================== Retrieval Test ====================
        const retrievalQuery = ref('如何学习Python编程');
        const retrievalTopK = ref(5);
        const retrievalLoading = ref(false);
        const showRetrievalEditor = ref(false);
        const retrievalPassagesJson = ref('');
        const retrievalResult = ref(null);
        const useRemoteForRetrieval = ref(false);
        const sampleRetrievalPassages = JSON.stringify([
            { id: '1', tag: '核心入门', text: 'Python是一门简单易学的编程语言，适合初学者入门', isRelevant: true },
            { id: '2', tag: '其他语言', text: 'Java是一种面向对象的编程语言，广泛应用于企业开发', isRelevant: false },
            { id: '3', tag: '实战教程', text: 'Python数据分析教程：从入门到精通', isRelevant: true },
            { id: '4', tag: '领域算法', text: '机器学习算法原理与实现', isRelevant: false },
            { id: '5', tag: '前端开发', text: 'JavaScript前端开发实战指南', isRelevant: false }
        ], null, 2);
        const loadSampleRetrieval = () => {
            retrievalPassagesJson.value = sampleRetrievalPassages;
            retrievalQuery.value = '如何学习Python编程';
        };
        const runRetrievalTest = async () => {
            if (!retrievalQuery.value)
                return;
            retrievalLoading.value = true;
            retrievalResult.value = null;
            try {
                let passages;
                try {
                    passages = JSON.parse(retrievalPassagesJson.value);
                }
                catch {
                    passages = JSON.parse(sampleRetrievalPassages);
                }
                if (useRemoteForRetrieval.value && remoteApi.value.baseUrl) {
                    // 使用远程 API
                    const result = await runRetrievalWithRemote(passages);
                    retrievalResult.value = result;
                }
                else {
                    // 使用本地 API
                    const res = await semanticTestApi.longText({
                        query: retrievalQuery.value,
                        passages,
                        topK: retrievalTopK.value
                    });
                    if (res.data && res.data.passages) {
                        res.data.passages = res.data.passages.map((p, i) => ({
                            rank: p.rank || (i + 1),
                            ...p
                        }));
                    }
                    retrievalResult.value = res.data;
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                retrievalLoading.value = false;
            }
        };
        // 使用远程 API 运行检索测试
        const runRetrievalWithRemote = async (passages) => {
            const startTime = performance.now();
            // 获取 query embedding
            const queryEmbeddings = await callEmbeddingApi([retrievalQuery.value]);
            const queryEmbedding = queryEmbeddings[0];
            const queryEmbeddingMs = performance.now() - startTime;
            // 批量获取 passage embeddings
            const passageTexts = passages.map(p => p.text || '');
            const passageEmbeddings = await callEmbeddingApi(passageTexts);
            const totalEmbeddingMs = performance.now() - startTime;
            // 计算相似度并排序
            const results = passages.map((p, i) => ({
                id: p.id,
                tag: p.tag,
                text: p.text,
                isRelevant: p.isRelevant,
                similarity: cosineSimilarity(queryEmbedding, passageEmbeddings[i]),
                tokenCount: estimateTokens(p.text || '')
            })).sort((a, b) => b.similarity - a.similarity);
            const topKResults = results.slice(0, retrievalTopK.value);
            // 计算指标
            const relevantInTopK = topKResults.filter(r => r.isRelevant).length;
            const totalRelevant = passages.filter(p => p.isRelevant).length;
            const precision = topKResults.length > 0 ? relevantInTopK / topKResults.length : 0;
            const recall = totalRelevant > 0 ? relevantInTopK / totalRelevant : 0;
            const f1Score = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
            // 计算 NDCG
            let dcg = 0, idcg = 0;
            topKResults.forEach((r, i) => {
                if (r.isRelevant)
                    dcg += 1 / Math.log2(i + 2);
            });
            const idealOrder = [...passages].filter(p => p.isRelevant).length;
            for (let i = 0; i < Math.min(idealOrder, retrievalTopK.value); i++) {
                idcg += 1 / Math.log2(i + 2);
            }
            const ndcg = idcg > 0 ? dcg / idcg : 0;
            // 计算 MRR
            let mrr = 0;
            for (let i = 0; i < results.length; i++) {
                if (results[i].isRelevant) {
                    mrr = 1 / (i + 1);
                    break;
                }
            }
            return {
                testType: 'long-text-remote',
                modelName: remoteApi.value.model || 'remote',
                timestamp: new Date().toISOString(),
                queryEmbeddingMs,
                totalEmbeddingMs,
                avgEmbeddingMs: totalEmbeddingMs / (passages.length + 1),
                passages: topKResults.map(r => ({
                    id: r.id,
                    tag: r.tag,
                    text: r.text,
                    similarity: r.similarity,
                    isRelevant: r.isRelevant,
                    tokenCount: r.tokenCount
                })),
                precision,
                recall,
                f1Score,
                ndcg,
                mrr
            };
        };
        const retrievalColumns = [
            {
                title: '排名',
                key: 'rank',
                width: 70,
                render: (row, index) => h('span', { style: 'font-weight: bold' }, `#${row.rank || (index + 1)}`)
            },
            {
                title: '相关性',
                key: 'isRelevant',
                width: 90,
                render: (row) => h(NTag, {
                    type: row.isRelevant ? 'success' : 'default',
                    size: 'small',
                    round: true
                }, {
                    default: () => row.isRelevant ? '✓ 相关' : '✕ 不相关'
                })
            },
            {
                title: '标签属性',
                key: 'tag',
                width: 140,
                render: (row) => {
                    if (!row.tag) return h('span', { style: 'color: #999; font-size: 12px' }, '-');
                    const t = String(row.tag);
                    const isNeg = t.includes('负例') || t.includes('废弃') || t.includes('越界') || t.includes('相反');
                    const isSuccess = t.includes('推荐') || t.includes('规范') || t.includes('完美') || t.includes('满足');
                    return h(NTag, {
                        type: isNeg ? 'error' : isSuccess ? 'success' : 'info',
                        size: 'small',
                        round: true,
                        bordered: false
                    }, { default: () => t });
                }
            },
            {
                title: '相似度',
                key: 'similarity',
                width: 105,
                render: (row) => {
                    const score = row.similarity || 0;
                    const type = score >= 0.7 ? 'success' : score >= 0.4 ? 'info' : score >= 0.25 ? 'warning' : 'default';
                    return h(NTag, {
                        type,
                        size: 'small',
                        bordered: false
                    }, { default: () => score.toFixed(4) });
                }
            },
            { title: 'Passage 内容', key: 'text', ellipsis: { tooltip: true } },
            { title: 'Tokens', key: 'tokenCount', width: 80 }
        ];

        // 检索测试预设与批量评测
        const retrievalPresets = ref([]);
        const activeRetrievalPreset = ref(null);
        const retrievalAllPresetsResults = ref([]);

        const parseRetrievalPassages = () => {
            customRetrievalError.value = '';
            if (!retrievalPassagesJson.value || !retrievalPassagesJson.value.trim()) {
                customRetrievalError.value = 'Passages 列表不能为空';
                return false;
            }
            try {
                const parsed = JSON.parse(retrievalPassagesJson.value);
                if (!Array.isArray(parsed) || parsed.length === 0) {
                    customRetrievalError.value = 'Passages 必须为包含 id, text, isRelevant 字段的非空 JSON 数组';
                    return false;
                }
                return true;
            } catch (err) {
                customRetrievalError.value = 'JSON 格式解析失败: ' + err.message;
                return false;
            }
        };

        const finishRetrievalEditing = () => {
            if (parseRetrievalPassages()) {
                showRetrievalEditor.value = false;
            }
        };

        const loadRetrievalPresets = async () => {
            try {
                const res = await semanticTestApi.getRetrievalPresets();
                retrievalPresets.value = res.data;
            } catch (e) {
                console.error('Failed to load retrieval presets:', e);
            }
        };

        const loadRetrievalPresetData = async (presetName) => {
            retrievalLoading.value = true;
            try {
                const res = await semanticTestApi.getRetrievalPreset(presetName);
                if (res.data) {
                    retrievalQuery.value = res.data.query || '';
                    retrievalTopK.value = res.data.topK || 5;
                    retrievalPassagesJson.value = JSON.stringify(res.data.passages || [], null, 2);
                }
            } catch (e) {
                console.error('Failed to load retrieval preset data:', e);
            } finally {
                retrievalLoading.value = false;
            }
        };

        const runRetrievalPreset = async (presetName) => {
            retrievalLoading.value = true;
            activeRetrievalPreset.value = presetName;
            retrievalResult.value = null;
            try {
                const res = await semanticTestApi.runRetrievalPreset(presetName);
                if (res.data && res.data.passages) {
                    res.data.passages = res.data.passages.map((p, i) => ({
                        rank: p.rank || (i + 1),
                        ...p
                    }));
                }
                retrievalResult.value = res.data;
            } catch (e) {
                console.error(e);
            } finally {
                retrievalLoading.value = false;
                activeRetrievalPreset.value = null;
            }
        };

        const retrievalPresetSummaryColumns = [
            { title: '测试套件', key: 'description', width: 150 },
            {
                title: 'Precision@K', key: 'precision', width: 95,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.precision || 0) >= 0.7 ? 'success' : (row.precision || 0) >= 0.5 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.precision || 0).toFixed(3) })
            },
            {
                title: 'Recall@K', key: 'recall', width: 90,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.recall || 0) >= 0.7 ? 'success' : (row.recall || 0) >= 0.5 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.recall || 0).toFixed(3) })
            },
            { title: 'F1 Score', key: 'f1Score', width: 85, render: (row) => (row.f1Score || 0).toFixed(3) },
            {
                title: 'NDCG', key: 'ndcg', width: 85,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.ndcg || 0) >= 0.8 ? 'success' : (row.ndcg || 0) >= 0.6 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.ndcg || 0).toFixed(3) })
            },
            {
                title: 'MRR', key: 'mrr', width: 85,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.mrr || 0) === 1.0 ? 'success' : (row.mrr || 0) >= 0.8 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.mrr || 0).toFixed(3) })
            },
            { title: '耗时', key: 'totalEmbeddingMs', width: 85, render: (row) => `${row.totalEmbeddingMs || 0} ms` },
            {
                title: '操作', key: 'actions', width: 90,
                render: (row) => h(NButton, {
                    size: 'tiny',
                    type: 'primary',
                    ghost: true,
                    onClick: () => { retrievalResult.value = row; }
                }, { default: () => '查看详情' })
            }
        ];

        const runAllRetrievalPresets = async () => {
            if (retrievalPresets.value.length === 0) {
                await loadRetrievalPresets();
            }
            retrievalLoading.value = true;
            retrievalResult.value = null;
            retrievalAllPresetsResults.value = [];
            const collected = [];
            try {
                for (const preset of retrievalPresets.value) {
                    activeRetrievalPreset.value = preset.name;
                    const res = await semanticTestApi.runRetrievalPreset(preset.name);
                    if (res && res.data) {
                        const data = res.data;
                        if (data.passages) {
                            data.passages = data.passages.map((p, i) => ({
                                rank: p.rank || (i + 1),
                                ...p
                            }));
                        }
                        collected.push({
                            name: preset.name,
                            description: preset.description,
                            passageCount: preset.passageCount,
                            relevantCount: preset.relevantCount,
                            ...data
                        });
                    }
                }
                retrievalAllPresetsResults.value = collected;
                if (collected.length > 0) {
                    retrievalResult.value = collected[0];
                }
            } catch (e) {
                console.error(e);
            } finally {
                retrievalLoading.value = false;
                activeRetrievalPreset.value = null;
            }
        };

        const exportRetrievalReport = () => {
            if (!retrievalResult.value) return;
            const r = retrievalResult.value;
            const lines = [
                `# 检索质量评测报告 (${r.modelName || 'embedding'})`,
                `> 生成时间: ${new Date().toLocaleString()}`,
                '',
                '## 1. 核心召回指标',
                '| 指标 | 评测分值 | 说明 |',
                '| :--- | :--- | :--- |',
                `| Precision@K | ${(r.precision || 0).toFixed(3)} | 准确率 (Top-K 中相关文档比例) |`,
                `| Recall@K | ${(r.recall || 0).toFixed(3)} | 召回率 (覆盖总相关文档比例) |`,
                `| F1 Score | ${(r.f1Score || 0).toFixed(3)} | 准确率与召回率调和平均 |`,
                `| NDCG | ${(r.ndcg || 0).toFixed(3)} | 归一化折损累计增益 |`,
                `| MRR | ${(r.mrr || 0).toFixed(3)} | 平均倒数排名 (首条命中排位) |`,
                `| Query耗时 | ${r.queryEmbeddingMs || 0} ms | 单查询向量编码耗时 |`,
                `| 总推理耗时 | ${r.totalEmbeddingMs || 0} ms | Query + Passages 全量耗时 |`,
                '',
                '## 2. 候选文档召回排序列表',
                '| 排名 | 相似度得分 | 标注真值 | 内容摘要 |',
                '| :--- | :--- | :--- | :--- |'
            ];
            (r.passages || []).forEach((p, idx) => {
                const rank = p.rank || (idx + 1);
                const rel = p.isRelevant ? '✓ 相关' : '✕ 不相关';
                const text = (p.text || '').replace(/\r?\n/g, ' ').substring(0, 80);
                lines.push(`| #${rank} | ${(p.similarity || 0).toFixed(4)} | ${rel} | ${text} |`);
            });
            const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `retrieval_report_${r.modelName || 'model'}_${Date.now()}.md`;
            a.click();
            URL.revokeObjectURL(url);
        };

        // ==================== Benchmark ====================
        const benchmark = ref({ textLength: 10000, chunkSize: 512, overlapSize: 50, batchSize: 8, concurrency: 4, includeCodeBlocks: true });
        const benchmarkLoading = ref(false);
        const benchmarkResult = ref(null);
        const useRemoteForBenchmark = ref(false);
        const benchmarkUsedRemote = ref(false);
        const runBenchmark = async () => {
            benchmarkLoading.value = true;
            benchmarkUsedRemote.value = useRemoteForBenchmark.value;
            try {
                if (useRemoteForBenchmark.value && remoteApi.value.baseUrl) {
                    // 使用远程 OpenAI 兼容 API 进行基准测试
                    const result = await runBenchmarkWithRemoteApi();
                    benchmarkResult.value = result;
                }
                else {
                    // 使用本地 API
                    const res = await performanceApi.benchmark(benchmark.value);
                    benchmarkResult.value = res.data;
                    if (res.data?.modelTelemetry) {
                        modelTelemetry.value = res.data.modelTelemetry;
                    } else {
                        loadModelTelemetry();
                    }
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                benchmarkLoading.value = false;
            }
        };
        // 使用远程 API 运行基准测试
        const runBenchmarkWithRemoteApi = async () => {
            const startTime = performance.now();
            // 生成测试文本
            const textLength = benchmark.value.textLength;
            const chunkSize = benchmark.value.chunkSize;
            const overlapSize = benchmark.value.overlapSize;
            const batchSize = benchmark.value.batchSize;
            // 生成模拟文本
            const generateStart = performance.now();
            const sampleText = generateSampleText(textLength);
            const textGenerationMs = performance.now() - generateStart;
            // 分段（简化版，实际分段逻辑在服务端）
            const chunkingStart = performance.now();
            const chunks = splitIntoChunks(sampleText, chunkSize, overlapSize);
            const chunkingMs = performance.now() - chunkingStart;
            // 并发调用远程 API
            const embeddingStart = performance.now();
            const batches = [];
            for (let i = 0; i < chunks.length; i += batchSize) {
                batches.push(chunks.slice(i, Math.min(i + batchSize, chunks.length)));
            }
            // 并发发送所有批次
            const concurrency = benchmark.value.concurrency || 4;
            const allEmbeddings = [];
            for (let i = 0; i < batches.length; i += concurrency) {
                const currentBatches = batches.slice(i, Math.min(i + concurrency, batches.length));
                const results = await Promise.all(currentBatches.map(batch => callEmbeddingApi(batch)));
                for (const embeddings of results) {
                    allEmbeddings.push(...embeddings);
                }
            }
            const embeddingMs = performance.now() - embeddingStart;
            const totalMs = performance.now() - startTime;
            const totalTokens = chunks.reduce((sum, c) => sum + estimateTokens(c), 0);
            const dimension = allEmbeddings[0]?.length || 512;
            return {
                textLength,
                chunkSize,
                overlapSize,
                batchSize,
                textGenerationMs,
                chunkingMs,
                tokenCountMs: 0,
                embeddingMs,
                storagePrepMs: 0,
                totalMs,
                chunkCount: chunks.length,
                totalChars: textLength,
                totalTokens,
                avgChunkTokens: Math.round(totalTokens / chunks.length),
                vectorCount: allEmbeddings.length,
                vectorDimension: dimension,
                tokensPerSecond: Math.round(totalTokens / (embeddingMs / 1000)),
                charsPerSecond: Math.round(textLength / (totalMs / 1000)),
                chunksPerSecond: chunks.length / (totalMs / 1000),
                vectorsPerSecond: allEmbeddings.length / (embeddingMs / 1000),
                estimatedMemoryMB: (allEmbeddings.length * dimension * 4) / (1024 * 1024)
            };
        };
        // 生成模拟文本
        const generateSampleText = (length) => {
            const words = ['人工智能', '机器学习', '深度学习', '自然语言处理', '向量数据库', '语义搜索', '知识图谱', '神经网络', 'Transformer', 'BERT模型'];
            let text = '';
            while (text.length < length) {
                text += words[Math.floor(Math.random() * words.length)] + '，';
                text += '这是一个关于' + words[Math.floor(Math.random() * words.length)] + '的示例文本。';
            }
            return text.slice(0, length);
        };
        // 简化分段
        const splitIntoChunks = (text, chunkSize, overlap) => {
            const chunks = [];
            let start = 0;
            while (start < text.length) {
                const end = Math.min(start + chunkSize, text.length);
                chunks.push(text.slice(start, end));
                start += chunkSize - overlap;
            }
            return chunks;
        };
        // 估算 token 数量
        const estimateTokens = (text) => {
            return Math.ceil(text.length / 2);
        };
        // ==================== Batch & Memory ====================
        const batchTextLength = ref(20000);
        const batchLoading = ref(false);
        const batchResult = ref(null);
        const batchColumns = [
            { title: 'BatchSize', key: 'batchSize', width: 100 },
            { title: 'Avg Time', key: 'avgTimeMs', width: 120, render: (row) => `${row.avgTimeMs.toFixed(1)} ms` },
            { title: 'Total Batches', key: 'totalBatches', width: 120 },
            { title: 'Est. Total', key: 'estimatedTotalTimeMs', render: (row) => {
                    const ms = row.estimatedTotalTimeMs;
                    return h(NTag, { size: 'small', type: ms < 5000 ? 'success' : ms < 30000 ? 'warning' : 'error' }, {
                        default: () => ms < 60000 ? `${ms.toFixed(0)} ms` : `${(ms / 1000).toFixed(1)} s`
                    });
                } }
        ];
        const runBatchTest = async () => {
            batchLoading.value = true;
            try {
                const res = await performanceApi.batchSizes({ textLength: batchTextLength.value });
                batchResult.value = res.data;
            }
            catch (e) {
                console.error(e);
            }
            finally {
                batchLoading.value = false;
            }
        };
        const memVectorCount = ref(1000);
        const memDimension = ref(512);
        const memLoading = ref(false);
        const memResult = ref(null);
        const runMemoryTest = async () => {
            memLoading.value = true;
            try {
                const res = await performanceApi.memoryTest(memVectorCount.value, memDimension.value);
                memResult.value = res.data;
            }
            catch (e) {
                console.error(e);
            }
            finally {
                memLoading.value = false;
            }
        };
        // ==================== Remote API Test (OpenAI-compatible) ====================
        const showApiConfig = ref(false);
        const apiConnected = ref(false);
        const apiTesting = ref(false);
        const apiTestResult = ref(null);
        const remoteApi = ref({
            baseUrl: '',
            apiKey: '',
            model: '',
            batchSize: 32
        });
        // Load saved config from localStorage
        const savedApiConfig = localStorage.getItem('reference-rag-remote-api');
        if (savedApiConfig) {
            try {
                Object.assign(remoteApi.value, JSON.parse(savedApiConfig));
            }
            catch { /* ignore */ }
        }
        const remoteLoading = ref(false);
        const remoteActiveCategory = ref(null);
        const remoteResults = ref([]);
        const saveApiConfig = () => {
            localStorage.setItem('reference-rag-remote-api', JSON.stringify(remoteApi.value));
        };
        // 测试 API 连接
        const testApiConnection = async () => {
            if (!remoteApi.value.baseUrl)
                return;
            apiTesting.value = true;
            apiTestResult.value = null;
            try {
                const startTime = performance.now();
                const embeddings = await callEmbeddingApi(['测试连接']);
                const latency = performance.now() - startTime;
                if (embeddings && embeddings[0] && embeddings[0].length > 0) {
                    apiConnected.value = true;
                    apiTestResult.value = {
                        success: true,
                        message: `连接成功，向量维度: ${embeddings[0].length}`,
                        latency: Math.round(latency)
                    };
                }
                else {
                    apiConnected.value = false;
                    apiTestResult.value = { success: false, message: '返回数据格式错误' };
                }
            }
            catch (e) {
                apiConnected.value = false;
                apiTestResult.value = { success: false, message: e.message || '连接失败' };
            }
            finally {
                apiTesting.value = false;
            }
        };
        const cosineSimilarity = (a, b) => {
            if (!a || !b || !a.length || !b.length)
                return 0;
            let dot = 0, normA = 0, normB = 0;
            const len = Math.min(a.length, b.length);
            for (let i = 0; i < len; i++) {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            const denom = Math.sqrt(normA) * Math.sqrt(normB);
            return denom > 0 ? dot / denom : 0;
        };
        const callEmbeddingApi = async (texts) => {
            saveApiConfig();
            const headers = { 'Content-Type': 'application/json' };
            if (remoteApi.value.apiKey) {
                headers['Authorization'] = `Bearer ${remoteApi.value.apiKey}`;
            }
            const baseUrl = remoteApi.value.baseUrl.replace(/\/+$/, '');
            const res = await fetch(`${baseUrl}/v1/embeddings`, {
                method: 'POST',
                headers,
                body: JSON.stringify({
                    model: remoteApi.value.model || 'bge-m3',
                    input: texts,
                    encoding_format: 'float'
                })
            });
            if (!res.ok) {
                const errText = await res.text();
                throw new Error(`API Error ${res.status}: ${errText}`);
            }
            const json = await res.json();
            const dataList = json.data ?? json.Data ?? [];
            const sorted = (Array.isArray(dataList) ? dataList : []).sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
            return sorted.map((item) => item.embedding ?? item.Embedding ?? []);
        };
        const processCategoryResults = async (category) => {
            // 每个 case 独立比较 query(text1) vs text(text2)，与 C# 参考代码一致
            // 批量发送所有文本对：[q1, t1, q2, t2, ...]
            const allTexts = [];
            for (const c of category.cases) {
                allTexts.push(c.query, c.text);
            }
            const embeddings = await callEmbeddingApi(allTexts);
            return category.cases.map((c, i) => {
                const actual = cosineSimilarity(embeddings[i * 2], embeddings[i * 2 + 1]);
                const deviation = Math.abs(actual - c.expectedSimilarity);
                const passed = evaluatePassByCategory(c.category, actual, c.expectedSimilarity);
                return {
                    text: `${c.query} ↔ ${c.text}`,
                    category: c.category,
                    expectedSimilarity: c.expectedSimilarity,
                    actualSimilarity: actual,
                    deviation,
                    passed
                };
            });
        };
        const runRemoteTest = async () => {
            remoteLoading.value = true;
            remoteActiveCategory.value = null;
            remoteResults.value = [];
            const startTime = performance.now();
            try {
                const allResults = [];
                for (const category of semanticCategories) {
                    const results = await processCategoryResults(category);
                    allResults.push(...results);
                }
                remoteResults.value = allResults;
                remoteTotalTime.value = Math.round(performance.now() - startTime);
            }
            catch (e) {
                console.error('Remote API test failed:', e);
                // Use naive-ui message via dynamic import workaround - just log for now
                alert(`API 测试失败: ${e.message}`);
            }
            finally {
                remoteLoading.value = false;
            }
        };
        const runRemoteCategoryTest = async (category) => {
            remoteLoading.value = true;
            remoteActiveCategory.value = category.label;
            remoteResults.value = [];
            const startTime = performance.now();
            try {
                remoteResults.value = await processCategoryResults(category);
                remoteTotalTime.value = Math.round(performance.now() - startTime);
            }
            catch (e) {
                console.error('Remote API test failed:', e);
                alert(`API 测试失败: ${e.message}`);
            }
            finally {
                remoteLoading.value = false;
            }
        };
        const runRemoteAllCategories = async () => {
            remoteLoading.value = true;
            remoteResults.value = [];
            const startTime = performance.now();
            const allResults = [];
            try {
                for (const category of semanticCategories) {
                    remoteActiveCategory.value = category.label;
                    const results = await processCategoryResults(category);
                    allResults.push(...results);
                }
                remoteResults.value = allResults;
                remoteTotalTime.value = Math.round(performance.now() - startTime);
            }
            catch (e) {
                console.error('Remote API test failed:', e);
                alert(`API 测试失败: ${e.message}`);
            }
            finally {
                remoteLoading.value = false;
                remoteActiveCategory.value = null;
            }
        };
        const remoteTotalTime = ref(0);
        const remoteMae = computed(() => {
            if (remoteResults.value.length === 0)
                return null;
            const sum = remoteResults.value.reduce((s, r) => s + r.deviation, 0);
            return sum / remoteResults.value.length;
        });
        const remoteRmse = computed(() => {
            const results = remoteResults.value;
            if (results.length === 0)
                return null;
            const sumSq = results.reduce((s, r) => s + r.deviation * r.deviation, 0);
            return Math.sqrt(sumSq / results.length);
        });
        const remoteCorrelation = computed(() => {
            const results = remoteResults.value;
            const n = results.length;
            if (n < 2)
                return null;
            // Pearson correlation between expected and actual similarity
            const ex = results.reduce((s, r) => s + r.expectedSimilarity, 0) / n;
            const ey = results.reduce((s, r) => s + r.actualSimilarity, 0) / n;
            let cov = 0, varX = 0, varY = 0;
            for (const r of results) {
                const dx = r.expectedSimilarity - ex;
                const dy = r.actualSimilarity - ey;
                cov += dx * dy;
                varX += dx * dx;
                varY += dy * dy;
            }
            const denom = Math.sqrt(varX) * Math.sqrt(varY);
            return denom > 0 ? cov / denom : 0;
        });
        const remoteOverallPassRate = computed(() => {
            const results = remoteResults.value;
            if (results.length === 0)
                return { passed: 0, total: 0, rate: '0%' };
            const passed = results.filter(r => r.passed).length;
            return { passed, total: results.length, rate: `${(passed / results.length * 100).toFixed(0)}%` };
        });
        const remoteCategoryStats = computed(() => {
            const map = new Map();
            for (const r of remoteResults.value) {
                const stat = map.get(r.category) || { total: 0, passed: 0, totalDeviation: 0 };
                stat.total++;
                if (r.passed)
                    stat.passed++;
                stat.totalDeviation += r.deviation;
                map.set(r.category, stat);
            }
            return Array.from(map.entries()).map(([category, s]) => ({
                category,
                total: s.total,
                passed: s.passed,
                passRate: `${(s.passed / s.total * 100).toFixed(0)}%`,
                avgDeviation: (s.totalDeviation / s.total).toFixed(3)
            }));
        });
        const remoteCategoryColumns = [
            { title: '类别', key: 'category', width: 140, ellipsis: { tooltip: true } },
            { title: '总数', key: 'total', width: 60 },
            { title: '通过', key: 'passed', width: 60 },
            { title: '通过率', key: 'passRate', width: 80, render: (row) => h(NTag, {
                    type: parseInt(row.passRate) >= 80 ? 'success' : parseInt(row.passRate) >= 50 ? 'warning' : 'error',
                    size: 'small'
                }, { default: () => row.passRate }) },
            { title: '平均偏差', key: 'avgDeviation', width: 90 },
        ];
        const remoteDetailColumns = [
            { title: '文本', key: 'text', ellipsis: { tooltip: true }, width: 200 },
            {
                title: '类别', key: 'category', width: 140,
                render: (row) => h(NTag, { size: 'small', type: 'info' }, { default: () => row.category })
            },
            { title: '期望', key: 'expectedSimilarity', width: 70, render: (row) => `${(row.expectedSimilarity * 100).toFixed(0)}%` },
            { title: '实际', key: 'actualSimilarity', width: 70, render: (row) => `${(row.actualSimilarity * 100).toFixed(1)}%` },
            {
                title: '偏差', key: 'deviation', width: 70,
                render: (row) => h(NTag, {
                    type: row.deviation <= 0.15 ? 'success' : row.deviation <= 0.3 ? 'warning' : 'error',
                    size: 'small'
                }, { default: () => row.deviation.toFixed(3) })
            },
            {
                title: '判定', key: 'passed', width: 60,
                render: (row) => h(NTag, { type: row.passed ? 'success' : 'error', size: 'small' }, {
                    default: () => row.passed ? '通过' : '未通过'
                })
            },
        ];
        // ==================== API Functionality Test ====================
        const apiTest = ref({
            baseUrl: '',
            apiKey: '',
            model: '',
            testText: '这是一个测试文本，用于验证 API 功能。'
        });
        const apiFuncTesting = ref(false);
        const apiFuncResult = ref(null);
        const apiHistory = ref([]);
        const apiBatchColumns = [
            { title: '批量大小', key: 'batchSize', width: 100 },
            { title: '耗时 (ms)', key: 'latencyMs', width: 120 },
            { title: '文本/秒', key: 'textsPerSecond', width: 120, render: (row) => row.textsPerSecond.toFixed(1) }
        ];
        const apiHistoryColumns = [
            { title: '时间', key: 'time', width: 160 },
            { title: 'URL', key: 'baseUrl', ellipsis: { tooltip: true }, width: 200 },
            { title: '模型', key: 'model', width: 120 },
            {
                title: '状态', key: 'success', width: 80,
                render: (row) => h(NTag, { type: row.success ? 'success' : 'error', size: 'small' }, {
                    default: () => row.success ? '成功' : '失败'
                })
            },
            { title: '耗时', key: 'latencyMs', width: 80, render: (row) => `${row.latencyMs}ms` }
        ];
        const loadPresetConfig = (preset) => {
            switch (preset) {
                case 'lmstudio':
                    apiTest.value.baseUrl = 'http://localhost:1234';
                    apiTest.value.model = '';
                    break;
                case 'ollama':
                    apiTest.value.baseUrl = 'http://localhost:11434';
                    apiTest.value.model = 'nomic-embed-text';
                    break;
                case 'openai':
                    apiTest.value.baseUrl = 'https://api.openai.com';
                    apiTest.value.model = 'text-embedding-3-small';
                    break;
            }
        };
        const callTestEmbeddingApi = async (texts) => {
            const headers = { 'Content-Type': 'application/json' };
            if (apiTest.value.apiKey) {
                headers['Authorization'] = `Bearer ${apiTest.value.apiKey}`;
            }
            const baseUrl = apiTest.value.baseUrl.replace(/\/+$/, '');
            const startTime = performance.now();
            const res = await fetch(`${baseUrl}/v1/embeddings`, {
                method: 'POST',
                headers,
                body: JSON.stringify({
                    model: apiTest.value.model || 'bge-m3',
                    input: texts,
                    encoding_format: 'float'
                })
            });
            const latencyMs = Math.round(performance.now() - startTime);
            if (!res.ok) {
                const errText = await res.text();
                throw new Error(`API Error ${res.status}: ${errText}`);
            }
            const json = await res.json();
            const dataList = json.data ?? json.Data ?? [];
            const sorted = (Array.isArray(dataList) ? dataList : []).sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
            const embeddings = sorted.map((item) => item.embedding ?? item.Embedding ?? []);
            return { embeddings, latencyMs, model: json.model };
        };
        const runApiFunctionTest = async () => {
            if (!apiTest.value.baseUrl || !apiTest.value.testText)
                return;
            apiFuncTesting.value = true;
            apiFuncResult.value = null;
            try {
                const { embeddings, latencyMs, model } = await callTestEmbeddingApi([apiTest.value.testText]);
                if (embeddings && embeddings[0] && embeddings[0].length > 0) {
                    const embedding = embeddings[0];
                    const sample = embedding.slice(0, 10);
                    apiFuncResult.value = {
                        success: true,
                        dimension: embedding.length,
                        latencyMs,
                        model,
                        tokenCount: Math.ceil(apiTest.value.testText.length / 2),
                        batchSupported: true,
                        embeddingSample: JSON.stringify(sample.map(v => v.toFixed(6)))
                    };
                    // 添加到历史记录
                    apiHistory.value.unshift({
                        time: new Date().toLocaleString(),
                        baseUrl: apiTest.value.baseUrl,
                        model: apiTest.value.model || model || 'unknown',
                        success: true,
                        latencyMs
                    });
                    if (apiHistory.value.length > 20)
                        apiHistory.value.pop();
                }
                else {
                    apiFuncResult.value = { success: false };
                }
            }
            catch (e) {
                apiFuncResult.value = { success: false };
                apiHistory.value.unshift({
                    time: new Date().toLocaleString(),
                    baseUrl: apiTest.value.baseUrl,
                    model: apiTest.value.model || 'unknown',
                    success: false,
                    latencyMs: 0
                });
                if (apiHistory.value.length > 20)
                    apiHistory.value.pop();
            }
            finally {
                apiFuncTesting.value = false;
            }
        };
        const runApiBatchTest = async () => {
            if (!apiTest.value.baseUrl)
                return;
            apiFuncTesting.value = true;
            const batchSizes = [1, 2, 4, 8, 16, 32];
            const results = [];
            try {
                for (const size of batchSizes) {
                    const texts = Array(size).fill('这是一个测试文本，用于验证批量处理性能。');
                    const { latencyMs } = await callTestEmbeddingApi(texts);
                    results.push({
                        batchSize: size,
                        latencyMs,
                        textsPerSecond: (size / latencyMs) * 1000
                    });
                }
                apiFuncResult.value = {
                    success: true,
                    batchResults: results
                };
            }
            catch (e) {
                console.error(e);
            }
            finally {
                apiFuncTesting.value = false;
            }
        };
        // ==================== Rerank Test ====================
        const rerankQuery = ref('如何学习Python编程');
        const rerankModelName = ref('');
        const rerankLoading = ref(false);
        const showCustomRerank = ref(false);
        const rerankDocumentsJson = ref('');
        const rerankDocuments = ref([]);
        const rerankResult = ref(null);
        const useRemoteForRerank = ref(false);
        // 预设测试套件
        const rerankPresets = ref([]);
        const activeRerankPreset = ref(null);
        const rerankStatistics = ref(null);
        // 性能基准测试
        const rerankBenchmarkLoading = ref(false);
        const benchmarkConcurrency = ref(4);
        const benchmarkTotalRequests = ref(100);
        const benchmarkDocCount = ref(5);
        const rerankBenchmarkResult = ref(null);
        const sampleRerankDocuments = JSON.stringify([
            { id: '1', text: 'Python是一门简单易学的编程语言，适合初学者入门', expectedRelevance: 0.95 },
            { id: '2', text: 'Java是一种面向对象的编程语言，广泛应用于企业开发', expectedRelevance: 0.3 },
            { id: '3', text: 'Python数据分析教程：从入门到精通', expectedRelevance: 0.85 },
            { id: '4', text: '机器学习算法原理与实现', expectedRelevance: 0.4 },
            { id: '5', text: 'JavaScript前端开发实战指南', expectedRelevance: 0.2 },
            { id: '6', text: 'Python编程从零开始学习指南', expectedRelevance: 0.9 },
            { id: '7', text: '深度学习框架TensorFlow实战', expectedRelevance: 0.35 }
        ], null, 2);
        const loadSampleRerankDocuments = () => {
            rerankDocumentsJson.value = sampleRerankDocuments;
            rerankQuery.value = '如何学习Python编程';
            parseRerankDocuments();
        };

        const sampleAntonymRerankDocuments = JSON.stringify([
            { id: '1', tag: '一线转复推荐', text: '临床治疗指南明确推荐：本抗心律失常处方药对轻中度室性早搏及阵发性心动过速具备优异疗效，推荐作为一线转复指征用药，并给出了具体口服治疗方案与安全监测指标。', expectedRelevance: 0.95 },
            { id: '2', tag: '规范用药指征', text: '规范化临床用药指征：针对无器质性心脏病的心律失常患者，推荐起始剂量为每日二次，连续监测心电图 PR 间期与 QTc 延长趋势以确保用药平稳。', expectedRelevance: 0.90 },
            { id: '3', tag: '药效对照研究', text: '药效动力学与临床对照研究：本品能有效抑制快速心室反应，在转复并维持正常窦性心律方面显著优于传统安慰剂对照组。', expectedRelevance: 0.80 },
            { id: '4', tag: '代谢与调量', text: '特殊人群代谢与剂量调整原则：老年患者及轻度肌酐清除率下降者，用药需减量 25%，并定期复查肝肾功能与血清电解质浓度。', expectedRelevance: 0.65 },
            { id: '5', tag: '生理监测', text: '该药物在非心血管病患中的生理代谢监测：对血脂、空腹血糖水平无明显临床干扰，但服药期间应避免剧烈运动与摄入高浓度咖啡因。', expectedRelevance: 0.40 },
            { id: '6', tag: '硬对立-极性相反', text: '本品绝对禁忌用于心源性休克、重度充血性心力衰竭及二度以上房室传导阻滞，使用可直接导致外周血管阻力崩溃与血压骤降，甚至诱发心脏停搏。', expectedRelevance: 0.15 },
            { id: '7', tag: '否定排他反转', text: '心血管急救指南强制规范：心源性休克与急性心衰发作期严禁使用任何负性肌力心律转复药物，不仅毫无适用指征，反而会直接加剧循环衰竭危及生命。', expectedRelevance: 0.12 },
            { id: '8', tag: '主谓与方向相反', text: '回顾性病历报告提示：心源性休克患者误服该药后，左室射血分数（LVEF）急剧恶化下降，导致恶性心律失常发生率剧增，与改善心功能目标完全相反。', expectedRelevance: 0.08 },
            { id: '9', tag: '无关诊疗', text: '急诊科针对上消化道大出血患者的急诊胃镜止血规范与质子泵抑制剂（PPI）高剂量静脉滴注临床诊疗路径。', expectedRelevance: 0.04 },
            { id: '10', tag: '无关领域', text: '国际空间站生命保障系统环控生保电解制氧装置与水循环再生设备运行维护规程与故障隔离手册。', expectedRelevance: 0.01 }
        ], null, 2);

        const loadAntonymSampleRerankDocuments = () => {
            rerankDocumentsJson.value = sampleAntonymRerankDocuments;
            rerankQuery.value = '关于该抗心律失常处方药在心源性休克与重度心衰患者中的临床用药禁忌与推荐指征';
            parseRerankDocuments();
        };
        const customRerankError = ref('');
        const customRetrievalError = ref('');
        const parseRerankDocuments = () => {
            customRerankError.value = '';
            if (!rerankDocumentsJson.value || !rerankDocumentsJson.value.trim()) {
                customRerankError.value = '文档列表不能为空';
                return false;
            }
            try {
                const parsed = JSON.parse(rerankDocumentsJson.value);
                if (!Array.isArray(parsed) || parsed.length === 0) {
                    customRerankError.value = '文档列表必须为非空的 JSON 数组 (例如 [{"id":"1","text":"...","expectedRelevance":0.9}])';
                    return false;
                }
                rerankDocuments.value = parsed;
                return true;
            }
            catch (err) {
                customRerankError.value = 'JSON 格式解析失败: ' + err.message;
                return false;
            }
        };
        // 加载预设测试套件
        const loadRerankPresets = async () => {
            try {
                const res = await rerankTestApi.getPresets();
                rerankPresets.value = res.data;
            }
            catch (e) {
                console.error('Failed to load rerank presets:', e);
            }
        };
        const activeRerankPresetKey = ref('');
        const showRerankFullMetrics = ref(false);

        const activeRerankPresetIndex = computed(() => {
            if (!rerankAllPresetsResults.value.length || !activeRerankPresetKey.value) return -1;
            return rerankAllPresetsResults.value.findIndex(p => p.name === activeRerankPresetKey.value);
        });

        const activeRerankPresetTitle = computed(() => {
            if (!rerankResult.value) return '重排测试结果与质量评估';
            const current = rerankAllPresetsResults.value.find(p => p.name === activeRerankPresetKey.value);
            if (current?.description) {
                return `重排测试详情: 【${current.description}】`;
            }
            if (rerankResult.value.description) {
                return `重排测试详情: 【${rerankResult.value.description}】`;
            }
            if (rerankResult.value.presetName) {
                const found = rerankPresets.value.find(p => p.name === rerankResult.value.presetName);
                return `重排测试详情: 【${found?.description || rerankResult.value.presetName}】`;
            }
            return '重排测试结果与质量评估 (自定义测试)';
        });

        const rerankPresetOptions = computed(() => {
            return rerankAllPresetsResults.value.map((p, idx) => ({
                label: `${idx + 1}. ${p.description || p.name} (NDCG: ${(p.ndcg || 0).toFixed(3)})`,
                value: p.name
            }));
        });

        const selectRerankSuite = (presetName, scroll = false) => {
            if (!presetName) return;
            activeRerankPresetKey.value = presetName;
            const item = rerankAllPresetsResults.value.find(p => p.name === presetName);
            if (item) {
                rerankResult.value = item;
            }
            if (scroll) {
                scrollToRerankDetail();
            }
        };

        const prevRerankSuite = () => {
            const idx = activeRerankPresetIndex.value;
            if (idx > 0) {
                const prev = rerankAllPresetsResults.value[idx - 1];
                selectRerankSuite(prev.name);
            }
        };

        const nextRerankSuite = () => {
            const idx = activeRerankPresetIndex.value;
            if (idx >= 0 && idx < rerankAllPresetsResults.value.length - 1) {
                const next = rerankAllPresetsResults.value[idx + 1];
                selectRerankSuite(next.name);
            }
        };

        const scrollToRerankDetail = () => {
            setTimeout(() => {
                const el = document.getElementById('rerank-detail-card');
                el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }, 60);
        };

        const scrollToRerankBoard = () => {
            const el = document.getElementById('rerank-board-card');
            el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        };

        const rerankRowProps = (row) => {
            const isActive = row.name === activeRerankPresetKey.value;
            return {
                style: {
                    cursor: 'pointer',
                    backgroundColor: isActive ? 'rgba(99, 226, 183, 0.12)' : undefined
                },
                onClick: () => {
                    selectRerankSuite(row.name, true);
                }
            };
        };

        // 运行单个预设测试
        const runRerankPreset = async (presetName) => {
            rerankLoading.value = true;
            activeRerankPreset.value = presetName;
            rerankResult.value = null;
            try {
                const res = await rerankTestApi.runPreset(presetName);
                if (res && res.data) {
                    const presetDef = rerankPresets.value.find(p => p.name === presetName);
                    const item = {
                        name: presetName,
                        description: presetDef?.description || presetName,
                        documentCount: presetDef?.documentCount || (res.data.documents ? res.data.documents.length : 0),
                        ...res.data
                    };
                    rerankResult.value = item;
                    activeRerankPresetKey.value = presetName;

                    // 增量合流到对比看板
                    const existingIdx = rerankAllPresetsResults.value.findIndex(p => p.name === presetName);
                    if (existingIdx !== -1) {
                        const next = [...rerankAllPresetsResults.value];
                        next[existingIdx] = item;
                        rerankAllPresetsResults.value = next;
                    } else if (rerankAllPresetsResults.value.length > 0) {
                        rerankAllPresetsResults.value = [...rerankAllPresetsResults.value, item];
                    }
                }
            }
            catch (e) {
                console.error(e);
            }
            finally {
                rerankLoading.value = false;
                activeRerankPreset.value = null;
            }
        };
        const rerankAllPresetsResults = ref([]);
        const rerankPresetSummaryColumns = [
            {
                title: '测试套件',
                key: 'description',
                width: 170,
                render: (row) => {
                    const isActive = row.name === activeRerankPresetKey.value;
                    return h(NSpace, { align: 'center', size: 6 }, {
                        default: () => [
                            isActive ? h(NTag, { size: 'tiny', type: 'success', round: true }, { default: () => '⭐ 查看中' }) : null,
                            h('span', { style: isActive ? 'color: var(--n-text-color); font-weight: bold;' : '' }, row.description || row.name)
                        ]
                    });
                }
            },
            {
                title: 'NDCG', key: 'ndcg', width: 85,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.ndcg || 0) >= 0.85 ? 'success' : (row.ndcg || 0) >= 0.7 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.ndcg || 0).toFixed(3) })
            },
            {
                title: 'MRR', key: 'mrr', width: 85,
                render: (row) => h(NTag, {
                    size: 'small',
                    type: (row.mrr || 0) === 1.0 ? 'success' : (row.mrr || 0) >= 0.8 ? 'info' : 'warning',
                    bordered: false
                }, { default: () => (row.mrr || 0).toFixed(3) })
            },
            { title: 'MAP', key: 'map', width: 80, render: (row) => (row.map || 0).toFixed(3) },
            {
                title: '排序准确度', key: 'rankingAccuracy', width: 95,
                render: (row) => (row.rankingAccuracy || 0).toFixed(3)
            },
            { title: 'MAE', key: 'meanAbsoluteError', width: 80, render: (row) => (row.meanAbsoluteError || 0).toFixed(3) },
            { title: '耗时', key: 'queryMs', width: 85, render: (row) => `${row.queryMs || 0} ms` },
            {
                title: '操作', key: 'actions', width: 150,
                render: (row) => {
                    const isActive = row.name === activeRerankPresetKey.value;
                    return h(NSpace, { size: 6 }, {
                        default: () => [
                            h(NButton, {
                                size: 'tiny',
                                type: isActive ? 'success' : 'primary',
                                ghost: !isActive,
                                onClick: (e) => {
                                    e.stopPropagation();
                                    selectRerankSuite(row.name, true);
                                }
                            }, { default: () => (isActive ? '当前查看' : '查看详情') }),
                            h(NButton, {
                                size: 'tiny',
                                secondary: true,
                                loading: rerankLoading.value && activeRerankPreset.value === row.name,
                                onClick: (e) => {
                                    e.stopPropagation();
                                    runRerankPreset(row.name);
                                }
                            }, { default: () => '重测' })
                        ]
                    });
                }
            }
        ];

        // 运行所有预设测试并形成横向对比报告
        const runAllRerankPresets = async () => {
            if (rerankPresets.value.length === 0) {
                await loadRerankPresets();
            }
            rerankLoading.value = true;
            rerankResult.value = null;
            rerankAllPresetsResults.value = [];
            const collected = [];
            try {
                for (const preset of rerankPresets.value) {
                    activeRerankPreset.value = preset.name;
                    const res = await rerankTestApi.runPreset(preset.name);
                    if (res && res.data) {
                        collected.push({
                            name: preset.name,
                            description: preset.description,
                            documentCount: preset.documentCount,
                            ...res.data
                        });
                    }
                }
                rerankAllPresetsResults.value = collected;
                if (collected.length > 0) {
                    selectRerankSuite(collected[0].name);
                }
                await loadRerankStatistics();
            }
            catch (e) {
                console.error(e);
            }
            finally {
                rerankLoading.value = false;
                activeRerankPreset.value = null;
            }
        };
        // 自定义重排测试
        const runCustomRerankTest = async () => {
            if (!parseRerankDocuments())
                return;
            if (!rerankQuery.value || !rerankQuery.value.trim()) {
                customRerankError.value = '查询文本不能为空';
                return;
            }
            rerankLoading.value = true;
            rerankResult.value = null;
            activeRerankPresetKey.value = '';
            try {
                if (useRemoteForRerank.value && remoteApi.value.baseUrl) {
                    const result = await runRerankWithRemoteApi();
                    rerankResult.value = result;
                    showCustomRerank.value = false;
                }
                else {
                    const res = await rerankTestApi.test({
                        query: rerankQuery.value.trim(),
                        documents: rerankDocuments.value,
                        modelName: rerankModelName.value || undefined
                    });
                    rerankResult.value = res.data;
                    showCustomRerank.value = false;
                }
            }
            catch (e) {
                customRerankError.value = '测试执行失败: ' + (e.message || '网络或服务异常');
                console.error(e);
            }
            finally {
                rerankLoading.value = false;
            }
        };
        // 加载测试统计
        const loadRerankStatistics = async () => {
            try {
                const res = await rerankTestApi.getStatistics();
                rerankStatistics.value = res.data;
            }
            catch (e) {
                console.error('Failed to load rerank statistics:', e);
            }
        };
        // 运行性能基准测试
        const runRerankBenchmark = async () => {
            rerankBenchmarkLoading.value = true;
            rerankBenchmarkResult.value = null;
            try {
                const res = await rerankTestApi.benchmark({
                    concurrency: benchmarkConcurrency.value,
                    totalRequests: benchmarkTotalRequests.value,
                    documentCount: benchmarkDocCount.value
                });
                rerankBenchmarkResult.value = res.data;
            }
            catch (e) {
                console.error(e);
            }
            finally {
                rerankBenchmarkLoading.value = false;
            }
        };
        // 初始化加载预设
        loadRerankPresets();
        loadRerankStatistics();
        // 使用远程 API 模拟重排测试（基于 embedding 相似度）
        const runRerankWithRemoteApi = async () => {
            const startTime = performance.now();
            // 获取 query embedding
            const queryEmbeddings = await callEmbeddingApi([rerankQuery.value]);
            const queryEmbedding = queryEmbeddings[0];
            const queryMs = performance.now() - startTime;
            // 获取所有文档的 embeddings
            const docTexts = rerankDocuments.value.map(d => d.text);
            const docEmbeddings = await callEmbeddingApi(docTexts);
            // 计算相似度并排序
            const results = rerankDocuments.value.map((d, i) => ({
                id: d.id,
                tag: d.tag,
                text: d.text,
                expectedRelevance: d.expectedRelevance,
                relevanceScore: cosineSimilarity(queryEmbedding, docEmbeddings[i])
            })).sort((a, b) => b.relevanceScore - a.relevanceScore);
            // 添加排名
            const rankedDocs = results.map((r, i) => ({
                id: r.id,
                tag: r.tag,
                text: r.text,
                relevanceScore: r.relevanceScore,
                expectedRelevance: r.expectedRelevance,
                deviation: r.expectedRelevance !== undefined ? Math.abs(r.relevanceScore - r.expectedRelevance) : undefined,
                rank: i + 1
            }));
            // 计算 NDCG
            let dcg = 0;
            let idcg = 0;
            rankedDocs.forEach((d, i) => {
                const rel = d.expectedRelevance ?? 0;
                dcg += (Math.pow(2, rel) - 1) / Math.log2(i + 2);
            });
            // Ideal DCG: 按 expectedRelevance 降序排列
            const idealOrder = [...rerankDocuments.value]
                .filter(d => d.expectedRelevance !== undefined)
                .sort((a, b) => (b.expectedRelevance ?? 0) - (a.expectedRelevance ?? 0));
            idealOrder.forEach((d, i) => {
                idcg += (Math.pow(2, d.expectedRelevance ?? 0) - 1) / Math.log2(i + 2);
            });
            const ndcg = idcg > 0 ? dcg / idcg : 0;
            // 计算 MRR (Mean Reciprocal Rank)
            let mrr = 0;
            for (let i = 0; i < rankedDocs.length; i++) {
                if ((rankedDocs[i].expectedRelevance ?? 0) >= 0.5) {
                    mrr = 1 / (i + 1);
                    break;
                }
            }
            // 计算 MAP (Mean Average Precision)
            let relevantCount = 0;
            let precisionSum = 0;
            for (let i = 0; i < rankedDocs.length; i++) {
                if ((rankedDocs[i].expectedRelevance ?? 0) >= 0.5) {
                    relevantCount++;
                    precisionSum += relevantCount / (i + 1);
                }
            }
            const totalRelevant = rerankDocuments.value.filter(d => (d.expectedRelevance ?? 0) >= 0.5).length;
            const map = totalRelevant > 0 ? precisionSum / totalRelevant : 0;
            // 计算 Ranking Accuracy (Spearman correlation)
            const expectedRanks = new Map();
            const sortedByExpected = [...rerankDocuments.value]
                .filter(d => d.expectedRelevance !== undefined)
                .sort((a, b) => (b.expectedRelevance ?? 0) - (a.expectedRelevance ?? 0));
            sortedByExpected.forEach((d, i) => {
                if (d.id)
                    expectedRanks.set(d.id, i + 1);
            });
            let rankingAccuracy = 0;
            const docsWithExpected = rankedDocs.filter(d => d.expectedRelevance !== undefined);
            if (docsWithExpected.length >= 2) {
                const n = docsWithExpected.length;
                let sumDiffSq = 0;
                docsWithExpected.forEach((d) => {
                    const expectedRank = expectedRanks.get(d.id) ?? (sortedByExpected.findIndex(doc => doc.id === d.id) + 1);
                    sumDiffSq += Math.pow(d.rank - expectedRank, 2);
                });
                rankingAccuracy = 1 - (6 * sumDiffSq) / (n * (n * n - 1));
            }
            // 计算 MAE
            const docsDeviation = rankedDocs.filter(d => d.deviation !== undefined);
            const meanAbsoluteError = docsDeviation.length > 0
                ? docsDeviation.reduce((sum, d) => sum + (d.deviation ?? 0), 0) / docsDeviation.length
                : 0;
            return {
                testType: 'rerank',
                modelName: remoteApi.value.model || 'remote-embedding',
                timestamp: new Date().toISOString(),
                queryMs,
                documents: rankedDocs,
                ndcg,
                mrr,
                map,
                rankingAccuracy,
                meanAbsoluteError
            };
        };
        
        // ==================== 性能参数解释与引导数据 ====================
        const rerankMetricsGuide = [
            { key: 'NDCG', name: '归一化折损累计增益', formula: 'DCG@K / IDCG@K', benchmark: '≥0.85 卓越 | 0.70-0.85 良好 | <0.70 偏差偏大', desc: '衡量相关文档排序位置与相关度权重的综合指标。越靠前的高相关文档得分贡献权重呈对数级增加。' },
            { key: 'MRR', name: '平均倒数排名', formula: '1 / 首个最相关文档排位', benchmark: '=1.000 最佳 | ≥0.80 优秀 | <0.60 易首屏失准', desc: '衡量第一个命中高相关文档出现的位置。若排在第 1 位则为 1.000。直接决定 RAG 首条参考质量。' },
            { key: 'MAP', name: '平均准确率均值', formula: 'Mean(Precision@Rank_i)', benchmark: '≥0.80 优秀 | 0.65-0.80 良好 | <0.65 分散', desc: '衡量所有相关文档在各截断点上的精度加权均值，反映全部相关结果能否密集聚拢在列表最前方。' },
            { key: '排序准确度', name: 'Spearman 秩相关系数', formula: '1 - 6Σd²/(n(n²-1))', benchmark: '≥0.85 极高 | 0.70-0.85 良好 | <0.70 存在逆序', desc: '衡量模型重排后的顺序与理论人工期望顺序的单调一致性。1.000 代表完全按期望先后排序。' },
            { key: 'MAE', name: '平均绝对误差', formula: 'Mean(|预测分 - 期望分|)', benchmark: '≤0.20 优秀校准 | 0.20-0.35 正常', desc: '模型输出的相关度置信分值与人工标定分数的绝对差值均值，反映模型的分数标定（Calibration）精度。' },
            { key: '查询耗时', name: 'Cross-Encoder 推理耗时', formula: 'Stopwatch 测量', benchmark: '<100ms 极佳交互 | 100-300ms 正常', desc: '重排模型处理本次查询与全部候选文档的端到端推理总耗时。' }
        ];

        const rerankEvaluation = computed(() => {
            if (!rerankResult.value) return null;
            const r = rerankResult.value;
            const ndcg = r.ndcg || 0;
            const mrr = r.mrr || 0;
            const map = r.map || 0;
            const accuracy = r.rankingAccuracy || 0;
            const queryMs = r.queryMs || 0;
            const mae = r.meanAbsoluteError || 0;

            let grade = 'B';
            let gradeType = 'info';
            let title = '基础可用级重排性能 (B 级)';

            if (ndcg >= 0.90 && mrr >= 0.90 && accuracy >= 0.85) {
                grade = 'S';
                gradeType = 'success';
                title = '卓越级重排性能 (S 级)';
            } else if (ndcg >= 0.80 && mrr >= 0.70) {
                grade = 'A';
                gradeType = 'success';
                title = '优秀生产级重排性能 (A 级)';
            } else if (ndcg < 0.65 || mrr < 0.50) {
                grade = 'C';
                gradeType = 'warning';
                title = '需关注调优 (C 级)';
            }

            const tips = [];
            if (mrr === 1.0) {
                tips.push('首选最相关文档 100% 精确置顶 (MRR=1.000)，直接满足问答首条高精命中需求');
            } else if (mrr >= 0.8) {
                tips.push(`首位文档排在第 ${Math.round(1 / mrr)} 位附近，整体排位靠前`);
            } else {
                tips.push('首选高相关文档未排在首屏，建议增大粗排召回候选倍率或选用标准 Cross-Encoder 模型');
            }

            if (accuracy >= 0.9) {
                tips.push('排序单调性完全契合预期，相对优先级分布严谨');
            } else if (accuracy < 0.7) {
                tips.push('候选排序存在逆序扰动，建议重点关注标注相关性较低文档的干扰');
            }

            if (queryMs <= 100) {
                tips.push(`端到端推理仅 ${queryMs}ms，处于极速实时交互区间（<100ms）`);
            } else if (queryMs <= 300) {
                tips.push(`推理延迟 ${queryMs}ms，处于生产环境标准响应区间`);
            } else {
                tips.push(`推理延迟 ${queryMs}ms 偏高，建议在模型管理中配置 GPU 加速或选用轻量量化模型`);
            }

            const highScores = (r.documents || []).filter(d => (d.relevanceScore || 0) >= 0.7).length;
            const cutoffTip = highScores > 0
                ? `推荐生产环境 RAG Top-K 提取前 ${Math.min(highScores, 5)} 篇高相关文档注入 LLM 上下文，可有效过滤无关背景杂音`
                : '当前测试文档相关度普遍较平缓，建议结合 BM25 混合检索进一步过滤';

            return { grade, gradeType, title, tips, cutoffTip, ndcg, mrr, map, accuracy, queryMs, mae };
        });

        const semanticMetricsGuide = [
            { key: '通过率', name: '多分类语义对抗通过率', formula: 'Passed / Total (100项)', benchmark: '≥90% 卓越通用 | 80%-90% 良好', desc: '覆盖同义词、反义词、多义词、中英跨语言、小数、符号等 100 项测试用例的综合达标比率。' },
            { key: 'MAE', name: '平均绝对误差', formula: 'Mean(|实际相似度 - 期望相似度|)', benchmark: '≤0.15 优秀拟合 | 0.15-0.25 正常', desc: '衡量向量余弦距离与人类主观真实语义相关性的平均偏差程度。' },
            { key: 'RMSE', name: '均方根误差', formula: 'Sqrt(Mean(误差²))', benchmark: '≤0.20 优秀 | >0.25 存在离群', desc: '对极端离群误判（如把完全无关文本算成超高相似度）更为敏感的二次方惩罚指标。' },
            { key: '相关系数', name: 'Pearson 线性相关度', formula: 'Cov(实际, 期望) / (Std(实际)*Std(期望))', benchmark: '≥0.85 极高区分度 | 0.70-0.85 良好', desc: '衡量向量空间中相似度梯度的阶梯区分灵敏度。越高说明对细微语义差别的把控越敏锐。' }
        ];

        const semanticEvaluation = computed(() => {
            if (!semanticResult.value) return null;
            const r = semanticResult.value;
            const rateStr = overallPassRate.value.rate || '0%';
            const rateNum = parseFloat(rateStr) || 0;
            const mae = r.meanAbsoluteError || 0;
            const rmse = r.rootMeanSquareError || 0;
            const corr = r.correlationCoefficient || 0;

            let grade = 'B';
            let gradeType = 'info';
            let title = '基础语义表征能力 (B 级)';

            if (rateNum >= 90 && corr >= 0.85) {
                grade = 'S';
                gradeType = 'success';
                title = '卓越通用语义表征 (S 级)';
            } else if (rateNum >= 80 && corr >= 0.70) {
                grade = 'A';
                gradeType = 'success';
                title = '优秀生产级向量表征 (A 级)';
            } else if (rateNum < 70) {
                grade = 'C';
                gradeType = 'warning';
                title = '需关注弱项调优 (C 级)';
            }

            const tips = [];
            tips.push(`分类测试综合通过率达 ${rateStr}（${overallPassRate.value.passed}/${overallPassRate.value.total}），整体语义对齐稳定`);
            if (corr >= 0.85) {
                tips.push(`相关系数 ${corr.toFixed(3)}：余弦距离与真实语义阶梯拟合度极佳`);
            } else {
                tips.push(`相关系数 ${corr.toFixed(3)}：对细微相似度阶梯的辨析存在非线性偏移`);
            }
            if (mae <= 0.15) {
                tips.push(`MAE ${mae.toFixed(3)}：相似度绝对分值精准贴合标定基准`);
            } else {
                tips.push(`MAE ${mae.toFixed(3)}：整体相似度量程存在全局偏置，建议依赖相对排序`);
            }

            return { grade, gradeType, title, tips, rateStr, mae: mae.toFixed(3), rmse: rmse.toFixed(3), corr: corr.toFixed(3) };
        });

        const retrievalMetricsGuide = [
            { key: 'Precision', name: '查准率 (精准率)', formula: '召回相关文档 / 召回文档总量', benchmark: '≥0.70 优秀 | <0.50 杂音偏多', desc: '在召回的前 K 个段落中，真正相关段落所占比例。高查准率能防止向大模型输入误导性上下文。' },
            { key: 'Recall', name: '查全率 (召回率)', formula: '召回相关文档 / 全部标注相关文档', benchmark: '≥0.80 优秀 | <0.60 易漏检', desc: '所有相关文档中被成功检索出的比例。高查全率能确保关键证据不遗漏。' },
            { key: 'F1 Score', name: 'F1 综合调和均值', formula: '2 * P * R / (P + R)', benchmark: '≥0.75 优秀平衡 | 0.60-0.75 良好', desc: 'Precision 与 Recall 的调和平均，综合评估召回能力在精度与广度之间的平衡度。' },
            { key: 'NDCG / MRR', name: '粗排顺序质量', formula: 'DCG@K / IDCG@K & 1/Rank', benchmark: '≥0.80 优秀', desc: '衡量向量检索在第一阶段粗排时的排位质量。' },
            { key: '编码耗时', name: 'Query Embedding 延迟', formula: 'Stopwatch 测量', benchmark: '<30ms 极速 | 30-80ms 正常', desc: '单次查询向量化推理耗时，是整个 RAG 检索链路的第一道响应时间。' }
        ];

        const retrievalEvaluation = computed(() => {
            if (!retrievalResult.value) return null;
            const r = retrievalResult.value;
            const f1 = r.f1Score || 0;
            const prec = r.precision || 0;
            const recall = r.recall || 0;
            const ndcg = r.ndcg || 0;
            const mrr = r.mrr || 0;
            const qMs = r.queryEmbeddingMs || 0;

            let grade = 'B';
            let gradeType = 'info';
            let title = '基础粗排召回 (B 级)';

            if (f1 >= 0.85 && ndcg >= 0.85) {
                grade = 'S';
                gradeType = 'success';
                title = '卓越召回质量 (S 级)';
            } else if (f1 >= 0.70 && ndcg >= 0.70) {
                grade = 'A';
                gradeType = 'success';
                title = '良好生产粗排 (A 级)';
            } else if (f1 < 0.60) {
                grade = 'C';
                gradeType = 'warning';
                title = '召回杂音偏高 (C 级)';
            }

            const tips = [];
            tips.push(`查准率 Precision=${(prec * 100).toFixed(1)}%，查全率 Recall=${(recall * 100).toFixed(1)}%，综合 F1 分数为 ${(f1 * 100).toFixed(1)}%`);
            tips.push(`Query 向量化耗时 ${qMs}ms，作为第一阶段语义粗排响应极速`);
            tips.push('💡 工程协同建议：嵌入模型作为 Bi-Encoder 侧重广度召回，建议将召回池 Top-K 设为 20~30，随后交由 Reranker 精排挑选前 3~5 条注入大模型');

            return { grade, gradeType, title, tips, prec: (prec * 100).toFixed(1) + '%', recall: (recall * 100).toFixed(1) + '%', f1: f1.toFixed(3), ndcg: ndcg.toFixed(3), mrr: mrr.toFixed(3), qMs };
        });

        
        const exportRerankReport = () => {
            if (!rerankResult.value) return;
            const r = rerankResult.value;
            const lines = [
                `# 重排模型测试报告 (${r.modelName || 'default'})`,
                `> 生成时间: ${new Date().toLocaleString()}`,
                '',
                '## 1. 核心评估指标',
                '| 指标 | 评测分值 | 说明 |',
                '| :--- | :--- | :--- |',
                `| NDCG | ${(r.ndcg || 0).toFixed(3)} | 归一化折损累计增益 |`,
                `| MRR | ${(r.mrr || 0).toFixed(3)} | 平均倒数排名 (首条命中排位) |`,
                `| MAP | ${(r.map || 0).toFixed(3)} | 平均准确率均值 |`,
                `| 排序准确度 | ${(r.rankingAccuracy || 0).toFixed(3)} | Spearman 秩相关度 |`,
                `| MAE | ${(r.meanAbsoluteError || 0).toFixed(3)} | 平均绝对误差 |`,
                `| 查询耗时 | ${r.queryMs || 0} ms | 端到端推理延迟 |`,
                '',
                '## 2. 候选文档精排列表',
                '| 排名 | 相关性得分 | 期望分值 | 偏差 | 文本内容 |',
                '| :--- | :--- | :--- | :--- | :--- |'
            ];
            (r.documents || []).forEach(d => {
                const exp = d.expectedRelevance != null ? d.expectedRelevance.toFixed(2) : '-';
                const dev = d.deviation != null ? d.deviation.toFixed(3) : '-';
                const text = (d.text || '').replace(/\r?\n/g, ' ').substring(0, 80);
                lines.push(`| #${d.rank || 0} | ${(d.relevanceScore || 0).toFixed(3)} | ${exp} | ${dev} | ${text} |`);
            });
            const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `rerank_report_${r.modelName || 'model'}_${Date.now()}.md`;
            a.click();
            URL.revokeObjectURL(url);
        };

        const exportSemanticReport = () => {
            if (!semanticResult.value) return;
            const r = semanticResult.value;
            const rate = overallPassRate.value.rate || '0%';
            const lines = [
                `# 语义相似度测试报告 (${r.modelName || 'embedding'})`,
                `> 生成时间: ${new Date().toLocaleString()}`,
                '',
                '## 1. 总体综合指标',
                `- **综合通过率**: ${overallPassRate.value.passed} / ${overallPassRate.value.total} (${rate})`,
                `- **平均绝对误差 (MAE)**: ${(r.meanAbsoluteError || 0).toFixed(3)}`,
                `- **均方根误差 (RMSE)**: ${(r.rootMeanSquareError || 0).toFixed(3)}`,
                `- **线性相关系数 (Pearson)**: ${(r.correlationCoefficient || 0).toFixed(3)}`,
                '',
                '## 2. 分类通过率统计',
                '| 类别 | 用例数 | 通过数 | 通过率 | 平均偏差 |',
                '| :--- | :--- | :--- | :--- | :--- |'
            ];
            (categoryStats.value || []).forEach(s => {
                lines.push(`| ${s.category} | ${s.total} | ${s.passed} | ${s.passRate} | ${s.avgDeviation} |`);
            });
            const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `semantic_report_${r.modelName || 'embedding'}_${Date.now()}.md`;
            a.click();
            URL.revokeObjectURL(url);
        };

        const rerankDocColumns = [
            {
                title: '排名',
                key: 'rank',
                width: 70,
                render: (row) => h('span', { style: 'font-weight: bold' }, `#${row.rank || 0}`)
            },
            {
                title: '相关性',
                key: 'relevanceScore',
                width: 100,
                render: (row) => {
                    const score = row.relevanceScore || 0;
                    const type = score >= 0.75 ? 'success' : score >= 0.5 ? 'info' : score >= 0.3 ? 'warning' : 'default';
                    return h(NTag, {
                        type,
                        size: 'small',
                        round: true
                    }, { default: () => score.toFixed(3) });
                }
            },
            {
                title: '标签属性',
                key: 'tag',
                width: 140,
                render: (row) => {
                    if (!row.tag) return h('span', { style: 'color: #999; font-size: 12px' }, '-');
                    const t = String(row.tag);
                    const isNeg = t.includes('硬负例') || t.includes('相反') || t.includes('反转') || t.includes('废弃') || t.includes('越界') || t.includes('硬对立') || t.includes('不达标');
                    const isWarn = t.includes('弱相关') || t.includes('非核心') || t.includes('边际') || t.includes('历史') || t.includes('替代');
                    const isSuccess = t.includes('推荐') || t.includes('规范') || t.includes('完美') || t.includes('满足') || t.includes('标准') || t.includes('指征');
                    return h(NTag, {
                        type: isNeg ? 'error' : isWarn ? 'warning' : isSuccess ? 'success' : 'info',
                        size: 'small',
                        round: true,
                        bordered: false
                    }, { default: () => t });
                }
            },
            {
                title: '期望',
                key: 'expectedRelevance',
                width: 80,
                render: (row) => (row.expectedRelevance !== undefined && row.expectedRelevance !== null) ? (row.expectedRelevance).toFixed(2) : '-'
            },
            {
                title: '偏差',
                key: 'deviation',
                width: 90,
                render: (row) => {
                    const dev = (row.deviation !== undefined && row.deviation !== null)
                        ? row.deviation
                        : (row.expectedRelevance !== undefined && row.expectedRelevance !== null && row.relevanceScore !== undefined
                            ? Math.abs(row.relevanceScore - row.expectedRelevance)
                            : null);
                    if (dev === null) return '-';
                    const devVal = dev.toFixed(3);
                    const isSmall = dev <= 0.15;
                    return h(NTag, {
                        size: 'small',
                        type: isSmall ? 'success' : dev <= 0.3 ? 'warning' : 'error',
                        bordered: false
                    }, { default: () => devVal });
                }
            },
            {
                title: '排序变动',
                key: 'rankStatus',
                width: 110,
                render: (row) => {
                    const rank = row.rank || 0;
                    const orig = (row.originalIndex !== undefined && row.originalIndex !== null) ? row.originalIndex + 1 : null;
                    if (rank === 1) {
                        return h(NTag, { size: 'small', type: 'success', round: true }, { default: () => '⭐ 首位命中' });
                    }
                    if (orig !== null) {
                        if (rank === orig) {
                            return h(NTag, { size: 'small', type: 'info', bordered: false }, { default: () => '✓ 顺位保持' });
                        }
                        if (rank < orig) {
                            return h(NTag, { size: 'small', type: 'warning', bordered: false }, { default: () => `↑ 前移 ${orig - rank}` });
                        }
                        return h(NTag, { size: 'small', type: 'default', bordered: false }, { default: () => `↓ 后移 ${rank - orig}` });
                    }
                    return '-';
                }
            },
            { title: '文档内容', key: 'text', ellipsis: { tooltip: true } },
            { title: 'ID', key: 'id', width: 80 }
        ];
        const __returned__ = { activeChatSubTab, chatEvalCases, chatEvalRunning, chatEvalProgress, chatEvalStats, chatEvalColumns, chatEvalDimensionColumns, runSingleChatEvalCase, runAllChatEvalCases, cancelChatEval, exportChatEvalReport, exportChatEvalCasesJson, chatTelemetry, chatTelemetryLoading, loadChatTelemetry, chatPresetPrompts, chatBenchmarkPrompt, chatBenchmarkMaxTokens, chatBenchmarkTemperature, chatBenchmarkStream, chatBenchmarkLoading, chatBenchmarkStreaming, chatBenchmarkOutput, chatBenchmarkMetrics, selectChatPreset, runChatBenchmark, chatBenchmarkEvaluation, showRerankFullMetrics, activeRerankPresetKey, activeRerankPresetIndex, activeRerankPresetTitle, rerankPresetOptions, selectRerankSuite, prevRerankSuite, nextRerankSuite, scrollToRerankDetail, scrollToRerankBoard, rerankRowProps, loadAntonymSampleRerankDocuments, sampleAntonymRerankDocuments, activeRerankSubTab, semanticDomainDefs, semanticDomainStats, ragWeightedScore, rerankDomainDefs, rerankDomainStats, rerankCompositeIndex,  activeModelTab, handleModelTabChange, finishRetrievalEditing, semanticTotalCount, retrievalPresets, activeRetrievalPreset, retrievalAllPresetsResults, retrievalPresetSummaryColumns, parseRetrievalPassages, loadRetrievalPresets, loadRetrievalPresetData, runRetrievalPreset, runAllRetrievalPresets, exportRetrievalReport, rerankAllPresetsResults, rerankPresetSummaryColumns, semanticProgress, customRerankError, customRetrievalError, exportRerankReport, exportSemanticReport, rerankMetricsGuide, rerankEvaluation, semanticMetricsGuide, semanticEvaluation, retrievalMetricsGuide, retrievalEvaluation, modelTelemetry, modelTelemetryLoading, loadModelTelemetry, rerankTelemetry, rerankTelemetryLoading, loadRerankTelemetry, loadAllTelemetry, activeTelemetryTab, probe, probeLoading, runProbe, semanticLoading, customSemanticLoading, showCustomSemantic, semanticResult, activeTestCategory, useRemoteForSemantic, customSemantic, customSemanticResult, categoryPassCriteria, defaultPassCriteria, evaluatePassByCategory, semanticCategories, runSemanticTest, runSemanticTestWithRemote, runCategoryTest, runCustomSemantic, getSimilarityType, categoryStats, overallPassRate, categoryColumns, semanticColumns, retrievalQuery, retrievalTopK, retrievalLoading, showRetrievalEditor, retrievalPassagesJson, retrievalResult, useRemoteForRetrieval, sampleRetrievalPassages, loadSampleRetrieval, runRetrievalTest, runRetrievalWithRemote, retrievalColumns, benchmark, benchmarkLoading, benchmarkResult, useRemoteForBenchmark, benchmarkUsedRemote, runBenchmark, runBenchmarkWithRemoteApi, generateSampleText, splitIntoChunks, estimateTokens, batchTextLength, batchLoading, batchResult, batchColumns, runBatchTest, memVectorCount, memDimension, memLoading, memResult, runMemoryTest, showApiConfig, apiConnected, apiTesting, apiTestResult, remoteApi, savedApiConfig, remoteLoading, remoteActiveCategory, remoteResults, saveApiConfig, testApiConnection, cosineSimilarity, callEmbeddingApi, processCategoryResults, runRemoteTest, runRemoteCategoryTest, runRemoteAllCategories, remoteTotalTime, remoteMae, remoteRmse, remoteCorrelation, remoteOverallPassRate, remoteCategoryStats, remoteCategoryColumns, remoteDetailColumns, apiTest, apiFuncTesting, apiFuncResult, apiHistory, apiBatchColumns, apiHistoryColumns, loadPresetConfig, callTestEmbeddingApi, runApiFunctionTest, runApiBatchTest, rerankQuery, rerankModelName, rerankLoading, showCustomRerank, rerankDocumentsJson, rerankDocuments, rerankResult, useRemoteForRerank, rerankPresets, activeRerankPreset, rerankStatistics, rerankBenchmarkLoading, benchmarkConcurrency, benchmarkTotalRequests, benchmarkDocCount, rerankBenchmarkResult, sampleRerankDocuments, loadSampleRerankDocuments, parseRerankDocuments, loadRerankPresets, runRerankPreset, runAllRerankPresets, runCustomRerankTest, loadRerankStatistics, runRerankBenchmark, runRerankWithRemoteApi, rerankDocColumns, get NTag() { return NTag; }, get NCode() { return NCode; }, get NDivider() { return NDivider; }, get NButton() { return NButton; }, get NSpace() { return NSpace; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"16\">\n    <!-- Primary Model Category Tab Navigation -->\n    <n-tabs v-model:value=\"activeModelTab\" type=\"segment\" size=\"large\" style=\"margin-bottom: 4px\" @update:value=\"handleModelTabChange\">\n      <n-tab-pane name=\"embedding\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600; font-size: 15px\">嵌入模型 (Embedding)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"info\">语义表征 / 向量检索 / 批处理性能</n-tag>\n          </n-space>\n        </template>\n\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- 嵌入模型运行状态与硬件加速 (Telemetry) -->\n    <n-card size=\"small\" style=\"margin-bottom: 8px\">\n      <template #header>\n        <n-space align=\"center\" :size=\"10\">\n          <span style=\"font-weight: 600\">嵌入模型运行状态与硬件加速</span>\n          <template v-if=\"modelTelemetry\">\n            <n-tag\n              :type=\"modelTelemetry.stateMachine.state === 2 ? 'success' : (modelTelemetry.stateMachine.state === 3 ? 'info' : (modelTelemetry.stateMachine.state === 5 ? 'error' : 'warning'))\"\n              size=\"small\"\n              round\n            >\n              {{ modelTelemetry.stateMachine.stateName }} ({{ modelTelemetry.stateMachine.stateDescription }})\n            </n-tag>\n            <n-tag\n              :type=\"modelTelemetry.hardware.isGpuMode ? 'success' : 'warning'\"\n              size=\"small\"\n              round\n            >\n              <template #icon>\n                <span>{{ modelTelemetry.hardware.isGpuMode ? '🚀' : '💻' }}</span>\n              </template>\n              {{ modelTelemetry.hardware.isGpuMode ? 'GPU 硬件加速' : 'CPU 计算模式' }}\n            </n-tag>\n            <n-tag v-if=\"modelTelemetry.hardware.isGpuMode && modelTelemetry.hardware.isFullOffload\" type=\"info\" size=\"small\">\n              全量 Offload\n            </n-tag>\n            <n-tag v-if=\"modelTelemetry?.configuration?.quantization\" size=\"small\" :bordered=\"false\">\n              {{ modelTelemetry.configuration.quantization }}\n            </n-tag>\n          </template>\n        </n-space>\n      </template>\n      <template #header-extra>\n        <n-space align=\"center\" :size=\"8\">\n          <n-text depth=\"3\" style=\"font-size: 11px\" v-if=\"modelTelemetry?.timestamp\">\n            更新于: {{ new Date(modelTelemetry.timestamp).toLocaleTimeString() }}\n          </n-text>\n          <n-button text size=\"small\" :loading=\"modelTelemetryLoading\" @click=\"loadModelTelemetry\">\n            刷新状态\n          </n-button>\n        </n-space>\n      </template>\n\n      <n-spin :show=\"modelTelemetryLoading\" style=\"min-height: 48px\">\n        <div v-if=\"modelTelemetry\">\n          <n-alert v-if=\"modelTelemetry.stateMachine.lastError\" type=\"error\" style=\"margin-bottom: 12px\" title=\"嵌入模型运行异常\">\n            {{ modelTelemetry.stateMachine.lastError }}\n          </n-alert>\n\n          <!-- 遥测参数网格 -->\n          <n-grid :cols=\"6\" :x-gap=\"12\" :y-gap=\"8\">\n            <n-gi>\n              <n-statistic label=\"活跃嵌入模型\">\n                <template #default>\n                  <n-text style=\"font-size: 12px; font-weight: bold; word-break: break-all\">\n                    {{ modelTelemetry.configuration.displayName || modelTelemetry.configuration.modelName || '—' }}\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"执行设备 / 后端\">\n                <template #default>\n                  <n-text style=\"font-size: 12px; word-break: break-all\">\n                    {{ modelTelemetry.hardware.executionDevice || modelTelemetry.configuration.backendDisplayName }}\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"GPU 卸载状态\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">\n                    {{ modelTelemetry.hardware.offloadDescription || (modelTelemetry.hardware.isGpuMode ? '启用' : '无') }}\n                  </n-text>\n                  <n-text v-if=\"modelTelemetry.hardware.totalVramGb\" depth=\"3\" style=\"font-size: 10px; display: block\">\n                    显存容量: {{ modelTelemetry.hardware.totalVramGb }} GB\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"向量维度\" :value=\"modelTelemetry.configuration.dimension\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最大上下文 (Context)\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">{{ modelTelemetry.configuration.contextSize }} tokens</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 10px; display: block\">BatchSize: {{ modelTelemetry.configuration.batchSize }}</n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"池化与规格\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">{{ modelTelemetry.configuration.poolingType }}</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 10px; display: block\">体积: {{ modelTelemetry.configuration.modelSizeFriendly }}</n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n        </div>\n        <n-text v-else-if=\"!modelTelemetryLoading\" depth=\"3\">暂无嵌入模型运行状态数据，点击「刷新状态」获取</n-text>\n      </n-spin>\n    </n-card>\n\n          <!-- Global OpenAI API Config -->\n    <n-card title=\"OpenAI 兼容 API 配置\" size=\"small\" v-if=\"showApiConfig\">\n      <template #header-extra>\n        <n-button text size=\"small\" @click=\"showApiConfig = false\">\n          <template #icon><n-icon><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"currentColor\"><path d=\"M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z\"/></svg></n-icon></template>\n        </n-button>\n      </template>\n      <n-grid :cols=\"5\" :x-gap=\"16\">\n        <n-gi>\n          <n-form-item label=\"Base URL\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.baseUrl\" placeholder=\"http://localhost:11434\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"API Key\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.apiKey\" type=\"password\" placeholder=\"留空则不发送\" show-password-on=\"click\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"模型名称\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.model\" placeholder=\"bge-m3\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"批量大小\" :show-feedback=\"false\">\n            <n-input-number v-model:value=\"remoteApi.batchSize\" :min=\"1\" :max=\"128\" style=\"width: 100%\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"状态\" :show-feedback=\"false\">\n            <n-space>\n              <n-tag :type=\"apiConnected ? 'success' : 'default'\" size=\"small\">\n                {{ apiConnected ? '已连接' : '未测试' }}\n              </n-tag>\n              <n-button size=\"small\" :loading=\"apiTesting\" @click=\"testApiConnection\">\n                测试连接\n              </n-button>\n            </n-space>\n          </n-form-item>\n        </n-gi>\n      </n-grid>\n      <n-text depth=\"3\" style=\"margin-top: 8px; display: block;\">\n        支持 Ollama、vLLM、Xinference、LM Studio、OpenAI 等任意 OpenAI 兼容接口。配置自动保存到 localStorage。\n      </n-text>\n    </n-card>\n\n    <n-space v-if=\"!showApiConfig && remoteApi.baseUrl\" align=\"center\" style=\"margin-bottom: 0\">\n      <n-tag type=\"info\" size=\"small\">\n        <template #icon><n-icon><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"currentColor\"><path d=\"M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z\"/></svg></n-icon></template>\n        {{ remoteApi.model || '远程API' }} - {{ remoteApi.baseUrl }}\n      </n-tag>\n      <n-button text size=\"small\" @click=\"showApiConfig = true\">修改配置</n-button>\n    </n-space>\n\n    <n-button v-if=\"!showApiConfig && !remoteApi.baseUrl\" size=\"small\" @click=\"showApiConfig = true\">\n      配置 OpenAI 兼容 API\n    </n-button>\n\n          <n-tabs type=\"line\" animated>\n            <n-tab-pane name=\"semantic\" tab=\"语义相似度测试\">\n\n        <!-- 模型诊断卡片 -->\n        <n-card size=\"small\" style=\"margin-bottom: 12px\">\n          <template #header>\n            <n-space align=\"center\" :size=\"8\">\n              <span>模型诊断</span>\n              <n-tag v-if=\"probe\" :type=\"probe.healthy ? 'success' : (probe.isSimulationMode ? 'error' : 'warning')\" size=\"small\">\n                {{ probe.isSimulationMode ? '⚠ 模拟模式' : (probe.healthy ? '✓ 正常' : '⚠ 异常') }}\n              </n-tag>\n              <n-tag v-else-if=\"probeLoading\" type=\"default\" size=\"small\">检测中…</n-tag>\n              <n-tag v-else type=\"default\" size=\"small\">休眠就绪</n-tag>\n            </n-space>\n          </template>\n          <template #header-extra>\n            <n-button type=\"primary\" ghost size=\"small\" :loading=\"probeLoading\" @click=\"runProbe\">运行模型探针</n-button>\n          </template>\n\n          <n-spin :show=\"probeLoading\" style=\"min-height: 48px\">\n            <div v-if=\"probe\">\n              <!-- 模拟模式警告 -->\n              <n-alert v-if=\"probe.isSimulationMode\" type=\"error\" style=\"margin-bottom: 10px\">\n                <template #header>模型未加载 — 正在使用随机向量</template>\n                当前嵌入服务运行在<strong>模拟模式</strong>，所有相似度均为随机值，测试结果无意义。\n                请在「模型管理」页切换到已下载的模型。\n              </n-alert>\n              <n-alert v-else-if=\"!probe.healthy && !probe.error\" type=\"warning\" style=\"margin-bottom: 10px\">\n                <template #header>向量质量异常</template>\n                自相似度 {{ probe.selfSimilarity.toFixed(3) }} / 高相似对 {{ probe.highSimilarityActual.toFixed(3) }}（期望 &gt;0.4）/ 低相似对 {{ probe.lowSimilarityActual.toFixed(3) }}（期望 &lt;0.35）。\n                可能原因：ONNX 格式不兼容、模型未完整转换、或该模型的相似度量程天然偏低。\n              </n-alert>\n              <n-alert v-if=\"probe.error\" type=\"error\" style=\"margin-bottom: 10px\">\n                探针失败：{{ probe.error }}\n              </n-alert>\n\n              <n-grid :cols=\"6\" :x-gap=\"12\">\n                <n-gi>\n                  <n-statistic label=\"模型\">\n                    <template #default>\n                      <n-text style=\"font-size: 12px; word-break: break-all\">{{ probe.modelName || '—' }}</n-text>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"维度\" :value=\"probe.dimension\" />\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"自相似度\">\n                    <template #default>\n                      <n-tag :type=\"probe.selfSimilarity > 0.99 ? 'success' : 'error'\" size=\"small\">\n                        {{ probe.selfSimilarity.toFixed(4) }}\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"同义词对\">\n                    <template #default>\n                      <n-tag :type=\"probe.highSimilarityActual >= 0.4 ? 'success' : 'warning'\" size=\"small\">\n                        {{ probe.highSimilarityActual.toFixed(3) }}\n                        <n-text depth=\"3\" style=\"font-size: 10px\"> / {{ probe.highSimilarityExpected.toFixed(2) }}</n-text>\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"无关词对\">\n                    <template #default>\n                      <n-tag :type=\"probe.lowSimilarityActual <= 0.35 ? 'success' : 'warning'\" size=\"small\">\n                        {{ probe.lowSimilarityActual.toFixed(3) }}\n                        <n-text depth=\"3\" style=\"font-size: 10px\"> / {{ probe.lowSimilarityExpected.toFixed(2) }}</n-text>\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"向量采样（前8维）\">\n                    <template #default>\n                      <n-text style=\"font-size: 10px; font-family: monospace; word-break: break-all\">\n                        [{{ probe.vectorSample.map(v => v.toFixed(3)).join(', ') }}]\n                      </n-text>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n              </n-grid>\n            </div>\n            <n-empty v-else-if=\"!probeLoading\" description=\"模型探针未运行（模型当前处于休眠释放状态，点击「运行模型探针」将唤醒模型执行自检诊断）\" style=\"padding: 12px 0\" />\n          </n-spin>\n        </n-card>\n\n        <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试模型的语义理解能力：同义词、反义词、多义词、跨语言、非对称问答、否定限定、小数与特殊格式等，涵盖 24 大语义分类评测集。</n-text>\n            <n-space align=\"center\">\n              <n-button type=\"primary\" :loading=\"semanticLoading\" @click=\"runSemanticTest\">\n                运行全部测试（{{ semanticTotalCount }} 项）\n              </n-button>\n              <n-button @click=\"showCustomSemantic = true\">\n                自定义测试\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForSemantic\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForSemantic && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <!-- 实时评测进度看板 -->\n        <n-card v-if=\"semanticLoading\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space vertical :size=\"6\">\n            <n-space justify=\"space-between\" align=\"center\">\n              <n-text depth=\"2\">\n                <span>正在评测 [{{ semanticProgress.currentCategory || '多分类语义用例' }}] ...</span>\n              </n-text>\n              <n-tag type=\"info\" size=\"small\" round>\n                {{ semanticProgress.current }} / {{ semanticProgress.total }} ({{ semanticProgress.percent }}%)\n              </n-tag>\n            </n-space>\n            <n-progress type=\"line\" :percentage=\"semanticProgress.percent\" :show-indicator=\"false\" processing status=\"info\" />\n          </n-space>\n        </n-card>\n\n        <!-- Category Buttons -->\n        <n-card title=\"分类测试\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"cat in semanticCategories\"\n              :key=\"cat.label\"\n              :loading=\"semanticLoading && activeTestCategory === cat.label\"\n              size=\"small\"\n              :type=\"activeTestCategory === cat.label ? 'primary' : 'default'\"\n              @click=\"runCategoryTest(cat)\"\n            >\n              {{ cat.label }}（{{ cat.cases.length }}）\n            </n-button>\n          </n-space>\n          <n-text v-if=\"activeTestCategory\" depth=\"3\" style=\"margin-top: 8px; display: block\">\n            {{ semanticCategories.find(c => c.label === activeTestCategory)?.description }}\n          </n-text>\n        </n-card>\n\n        <!-- Custom Semantic Test Dialog -->\n        <n-modal v-model:show=\"showCustomSemantic\" preset=\"dialog\" title=\"自定义语义测试\">\n          <n-space vertical>\n            <n-form-item label=\"文本 1\">\n              <n-input v-model:value=\"customSemantic.text1\" placeholder=\"输入第一段文本\" />\n            </n-form-item>\n            <n-form-item label=\"文本 2\">\n              <n-input v-model:value=\"customSemantic.text2\" placeholder=\"输入第二段文本\" />\n            </n-form-item>\n            <n-form-item label=\"期望相似度\">\n              <n-slider v-model:value=\"customSemantic.expected\" :min=\"0\" :max=\"100\" :step=\"5\" />\n            </n-form-item>\n            <n-button type=\"primary\" :loading=\"customSemanticLoading\" @click=\"runCustomSemantic\">\n              测试\n            </n-button>\n          </n-space>\n          <n-card v-if=\"customSemanticResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n            <n-descriptions :column=\"2\">\n              <n-descriptions-item label=\"实际相似度\">\n                <n-tag :type=\"getSimilarityType(customSemanticResult.candidates?.[0]?.similarity || 0, customSemantic.expected / 100)\">\n                  {{ ((customSemanticResult.candidates?.[0]?.similarity || 0) * 100).toFixed(1) }}%\n                </n-tag>\n              </n-descriptions-item>\n              <n-descriptions-item label=\"期望相似度\">{{ customSemantic.expected }}%</n-descriptions-item>\n              <n-descriptions-item label=\"偏差\">\n                {{ (customSemanticResult.candidates?.[0]?.deviation ?? 0).toFixed(3) }}\n              </n-descriptions-item>\n              <n-descriptions-item label=\"Token 计数\">\n                {{ customSemanticResult.queryTokenCount || '-' }}\n              </n-descriptions-item>\n            </n-descriptions>\n          </n-card>\n        </n-modal>\n\n        <!-- Results -->\n        <n-card v-if=\"semanticResult\" title=\"语义测试结果与质量评估\" style=\"margin-top: 16px\">\n          <template #header-extra>\n            <n-space align=\"center\" :size=\"8\">\n              <n-button size=\"small\" ghost type=\"primary\" @click=\"exportSemanticReport\">导出评测报告</n-button>\n              <n-tag v-if=\"semanticEvaluation\" :type=\"semanticEvaluation.gradeType\" size=\"small\" round>\n                {{ semanticEvaluation.grade }} 级综合评估\n              </n-tag>\n            </n-space>\n          </template>\n\n          <!-- 诊断与落地引导面板 -->\n          <n-alert v-if=\"semanticEvaluation\" :type=\"semanticEvaluation.gradeType\" style=\"margin-bottom: 16px\" :title=\"semanticEvaluation.title\">\n            <div style=\"margin-top: 6px; line-height: 1.8\">\n              <ul style=\"margin: 0; padding-left: 18px\">\n                <li v-for=\"(tip, i) in semanticEvaluation.tips\" :key=\"i\">{{ tip }}</li>\n              </ul>\n            </div>\n          </n-alert>\n\n          <!-- 参数说明对照折叠面板 -->\n          <n-collapse style=\"margin-bottom: 16px\">\n            <n-collapse-item title=\"📘 语义相似度核心指标解读指南\" name=\"semantic-guide\">\n              <n-table size=\"small\" :single-line=\"false\" style=\"margin-top: 8px\">\n                <thead>\n                  <tr><th>指标</th><th>全称与定义</th><th>标准范围</th><th>在 RAG 系统中的意义</th></tr>\n                </thead>\n                <tbody>\n                  <tr v-for=\"m in semanticMetricsGuide\" :key=\"m.key\">\n                    <td><strong>{{ m.key }}</strong></td>\n                    <td>{{ m.name }}<br/><n-text depth=\"3\" style=\"font-size: 11px\">{{ m.formula }}</n-text></td>\n                    <td><n-tag size=\"small\" :bordered=\"false\">{{ m.benchmark }}</n-tag></td>\n                    <td>{{ m.desc }}</td>\n                  </tr>\n                </tbody>\n              </n-table>\n            </n-collapse-item>\n          </n-collapse>\n          <!-- 四大能力域多维画像看板 -->\n          <n-card title=\"嵌入模型四大能力域评测画像\" size=\"small\" style=\"margin-bottom: 16px; background: rgba(128, 128, 128, 0.03)\">\n            <template #header-extra>\n              <n-space align=\"center\" :size=\"8\">\n                <span style=\"font-size: 13px; color: #888\">RAG 综合推荐指数:</span>\n                <n-tag :type=\"parseFloat(ragWeightedScore) >= 80 ? 'success' : (parseFloat(ragWeightedScore) >= 60 ? 'info' : 'warning')\" size=\"medium\" round>\n                  <span style=\"font-weight: bold; font-size: 15px\">{{ ragWeightedScore }}</span> / 100\n                </n-tag>\n              </n-space>\n            </template>\n\n            <n-grid :cols=\"4\" :x-gap=\"12\" :y-gap=\"12\" style=\"margin-bottom: 12px\">\n              <n-gi v-for=\"domain in semanticDomainStats\" :key=\"domain.key\">\n                <n-card size=\"small\" :bordered=\"true\" style=\"height: 100%\">\n                  <n-space justify=\"space-between\" align=\"center\" style=\"margin-bottom: 8px\">\n                    <n-space align=\"center\" :size=\"6\">\n                      <span style=\"font-size: 16px\">{{ domain.icon }}</span>\n                      <span style=\"font-weight: 600; font-size: 13px\">{{ domain.name }}</span>\n                    </n-space>\n                    <n-tag :type=\"domain.tagType\" size=\"tiny\" round>{{ domain.rating }}</n-tag>\n                  </n-space>\n                  <n-progress\n                    type=\"line\"\n                    :percentage=\"domain.passRate\"\n                    :status=\"domain.passRate >= 80 ? 'success' : (domain.passRate >= 60 ? 'info' : 'warning')\"\n                    :show-indicator=\"false\"\n                    style=\"margin-bottom: 8px\"\n                  />\n                  <n-space justify=\"space-between\" align=\"center\" style=\"font-size: 12px\">\n                    <span style=\"color: #888\">通过率: <strong style=\"color: var(--n-text-color)\">{{ domain.passRate }}%</strong> ({{ domain.passed }}/{{ domain.total }})</span>\n                    <span style=\"color: #888\">权重: {{ (domain.weight * 100).toFixed(0) }}%</span>\n                  </n-space>\n                  <div style=\"font-size: 11px; color: #888; margin-top: 6px; line-height: 1.4\">\n                    {{ domain.description }}\n                  </div>\n                </n-card>\n              </n-gi>\n            </n-grid>\n\n            <!-- 智能架构协同引导提示 -->\n            <n-alert type=\"info\" :show-icon=\"true\" style=\"margin-top: 8px\">\n              <template #header>💡 RAG 架构选型与协同诊断分析</template>\n              <div style=\"font-size: 12px; line-height: 1.6\">\n                <span v-if=\"semanticDomainStats.find(d => d.key === 'logic_adversarial')?.passRate < 70\">\n                  当前嵌入模型在<strong>「RAG 核心检索」</strong>表现稳定，但在<strong>「逻辑对抗与排他（反义/否定）」</strong>通过率为 {{ semanticDomainStats.find(d => d.key === 'logic_adversarial')?.passRate }}%。\n                  这是双塔（Bi-Encoder）模型因缺少 Token 级交叉注意力机制的常见分布特征。<br>\n                  <strong>工程落地建议</strong>：无需更换嵌入模型，只需在 RAG 管道中配置 <strong>Cross-Encoder 重排模型（Reranker）</strong> 进行二次精排过滤，即可低成本消除反义干扰与幻觉！\n                </span>\n                <span v-else>\n                  当前嵌入模型在核心召回与逻辑排他上均具备均衡的区分度，适于直接用于向量检索与问答去重任务。\n                </span>\n              </div>\n            </n-alert>\n          </n-card>\n\n          <!-- Summary -->\n          <n-space style=\"margin-bottom: 16px\">\n            <n-tag type=\"success\" size=\"large\">\n              通过: {{ overallPassRate.passed }} / {{ overallPassRate.total }}（{{ overallPassRate.rate }}）\n            </n-tag>\n            <n-tag v-if=\"semanticResult.meanAbsoluteError != null\" size=\"large\">\n              MAE: {{ semanticResult.meanAbsoluteError.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"semanticResult.rootMeanSquareError != null\" size=\"large\">\n              RMSE: {{ semanticResult.rootMeanSquareError.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"semanticResult.correlationCoefficient != null\" size=\"large\">\n              相关性: {{ semanticResult.correlationCoefficient.toFixed(3) }}\n            </n-tag>\n          </n-space>\n\n          <!-- Category Stats -->\n          <n-data-table\n            v-if=\"categoryStats.length > 0\"\n            :columns=\"categoryColumns\"\n            :data=\"categoryStats\"\n            :bordered=\"false\"\n            size=\"small\"\n            style=\"margin-bottom: 16px\"\n          />\n\n          <!-- Detail Table -->\n          <n-data-table\n            :columns=\"semanticColumns\"\n            :data=\"semanticResult.candidates || []\"\n            :bordered=\"false\"\n            size=\"small\"\n            :max-height=\"400\"\n            :scroll-x=\"800\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Retrieval Quality Test (long text) -->\n      <n-tab-pane name=\"retrieval\" tab=\"检索质量测试\">\n        <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试模型的文档检索能力：给定 query 和标注相关性的 passages，评估排序准确性。</n-text>\n            <n-grid :cols=\"3\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"查询文本\">\n                  <n-input v-model:value=\"retrievalQuery\" placeholder=\"输入查询文本\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"Top K\">\n                  <n-input-number v-model:value=\"retrievalTopK\" :min=\"1\" :max=\"20\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"Passages (JSON)\">\n                  <n-button size=\"small\" @click=\"showRetrievalEditor = true\">编辑 Passages</n-button>\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"retrievalLoading\" @click=\"runRetrievalTest\">\n                运行检索测试\n              </n-button>\n              <n-button @click=\"loadSampleRetrieval\">\n                加载示例数据\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForRetrieval\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForRetrieval && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        \n        <!-- 预设套件一键运行与横向对比 -->\n        <n-card title=\"内置真实检索测试套件\" size=\"small\" style=\"margin-top: 12px\">\n          <template #header-extra>\n            <n-space>\n              <n-button\n                size=\"small\"\n                type=\"primary\"\n                ghost\n                :loading=\"retrievalLoading && activeRetrievalPreset === null\"\n                @click=\"runAllRetrievalPresets\"\n              >\n                运行全部套件横向对比\n              </n-button>\n            </n-space>\n          </template>\n          <n-space>\n            <n-button-group v-for=\"preset in retrievalPresets\" :key=\"preset.name\" size=\"small\">\n              <n-button\n                type=\"primary\"\n                :loading=\"retrievalLoading && activeRetrievalPreset === preset.name\"\n                @click=\"runRetrievalPreset(preset.name)\"\n              >\n                运行: {{ preset.description }}\n              </n-button>\n              <n-button\n                secondary\n                @click=\"loadRetrievalPresetData(preset.name)\"\n              >\n                加载并自定义\n              </n-button>\n            </n-button-group>\n          </n-space>\n          <n-text depth=\"3\" style=\"margin-top: 8px; display: block\">\n            内置技术文档、企业行政与跨语言 10 篇真实长文本检索套件，支持一键评测或载入编辑器做二次调优。\n          </n-text>\n        </n-card>\n\n        <!-- 全部套件横向对比看板 -->\n        <n-card v-if=\"retrievalAllPresetsResults.length > 0\" title=\"检索套件横向对比总览\" size=\"small\" style=\"margin-top: 12px\">\n          <n-data-table\n            :columns=\"retrievalPresetSummaryColumns\"\n            :data=\"retrievalAllPresetsResults\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n\n        <n-modal v-model:show=\"showRetrievalEditor\" preset=\"dialog\" title=\"编辑 Passages (JSON)\" style=\"width: 60vw\">\n          <n-space vertical>\n            <n-alert v-if=\"customRetrievalError\" type=\"error\" style=\"margin-bottom: 8px\">{{ customRetrievalError }}</n-alert>\n            <n-input\n              v-model:value=\"retrievalPassagesJson\"\n              type=\"textarea\"\n              :rows=\"12\"\n              placeholder='[{\"id\":\"1\",\"text\":\"文档内容\",\"isRelevant\":true}]'\n              style=\"font-family: monospace\"\n            />\n            <n-space justify=\"end\">\n              <n-button type=\"primary\" size=\"small\" @click=\"finishRetrievalEditing\">完成编辑</n-button>\n            </n-space>\n          </n-space>\n        </n-modal>\n\n        <n-card v-if=\"retrievalResult\" title=\"检索召回质量与性能结果\" style=\"margin-top: 16px\">\n          <template #header-extra>\n            <n-space align=\"center\" :size=\"8\">\n              <n-button size=\"small\" ghost type=\"primary\" @click=\"exportRetrievalReport\">导出评测报告</n-button>\n              <n-tag v-if=\"retrievalEvaluation\" :type=\"retrievalEvaluation.gradeType\" size=\"small\" round>\n              {{ retrievalEvaluation.grade }} 级召回质量\n              </n-tag>\n            </n-space>\n          </template>\n\n          <!-- 诊断与落地引导面板 -->\n          <n-alert v-if=\"retrievalEvaluation\" :type=\"retrievalEvaluation.gradeType\" style=\"margin-bottom: 16px\" :title=\"retrievalEvaluation.title\">\n            <div style=\"margin-top: 6px; line-height: 1.8\">\n              <ul style=\"margin: 0; padding-left: 18px\">\n                <li v-for=\"(tip, i) in retrievalEvaluation.tips\" :key=\"i\">{{ tip }}</li>\n              </ul>\n            </div>\n          </n-alert>\n\n          <!-- 参数说明对照折叠面板 -->\n          <n-collapse style=\"margin-bottom: 16px\">\n            <n-collapse-item title=\"📘 检索召回性能参数解读指南\" name=\"retrieval-guide\">\n              <n-table size=\"small\" :single-line=\"false\" style=\"margin-top: 8px\">\n                <thead>\n                  <tr><th>指标</th><th>全称与定义</th><th>标准范围</th><th>在 RAG 粗排中的工程意义</th></tr>\n                </thead>\n                <tbody>\n                  <tr v-for=\"m in retrievalMetricsGuide\" :key=\"m.key\">\n                    <td><strong>{{ m.key }}</strong></td>\n                    <td>{{ m.name }}<br/><n-text depth=\"3\" style=\"font-size: 11px\">{{ m.formula }}</n-text></td>\n                    <td><n-tag size=\"small\" :bordered=\"false\">{{ m.benchmark }}</n-tag></td>\n                    <td>{{ m.desc }}</td>\n                  </tr>\n                </tbody>\n              </n-table>\n            </n-collapse-item>\n          </n-collapse>\n          <!-- Metrics -->\n          <n-grid :cols=\"5\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n            <n-gi>\n              <n-statistic label=\"Precision\" :value=\"(retrievalResult.precision || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Recall\" :value=\"(retrievalResult.recall || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"F1 Score\" :value=\"(retrievalResult.f1Score || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"NDCG\" :value=\"(retrievalResult.ndcg || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MRR\" :value=\"(retrievalResult.mrr || 0).toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"3\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"Query Embedding\" :value=\"retrievalResult.queryEmbeddingMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Total Embedding\" :value=\"retrievalResult.totalEmbeddingMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Avg Embedding\" :value=\"(retrievalResult.avgEmbeddingMs || 0).toFixed(1)\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n          </n-grid>\n\n          <!-- Passage Ranking -->\n          <n-divider>Passage 排序</n-divider>\n          <n-data-table\n            :columns=\"retrievalColumns\"\n            :data=\"retrievalResult.passages || []\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Benchmark -->\n      <n-tab-pane name=\"benchmark\" tab=\"性能基准\">\n        <n-card>\n          <n-space vertical>\n            <n-grid :cols=\"useRemoteForBenchmark ? 6 : 5\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"文本长度\">\n                  <n-input-number v-model:value=\"benchmark.textLength\" :min=\"1000\" :max=\"1000000\" :step=\"10000\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"分段大小\">\n                  <n-input-number v-model:value=\"benchmark.chunkSize\" :min=\"64\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"重叠大小\">\n                  <n-input-number v-model:value=\"benchmark.overlapSize\" :min=\"0\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"批量大小\">\n                  <n-input-number v-model:value=\"benchmark.batchSize\" :min=\"1\" :max=\"128\" :step=\"1\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi v-if=\"useRemoteForBenchmark\">\n                <n-form-item label=\"并发数\">\n                  <n-input-number v-model:value=\"benchmark.concurrency\" :min=\"1\" :max=\"32\" :step=\"1\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"包含代码块\">\n                  <n-switch v-model:value=\"benchmark.includeCodeBlocks\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"benchmarkLoading\" @click=\"runBenchmark\">\n                运行基准测试\n              </n-button>\n              <n-checkbox v-model:checked=\"useRemoteForBenchmark\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForBenchmark && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"benchmarkResult\" title=\"基准测试结果\" style=\"margin-top: 16px\">\n          <n-space style=\"margin-bottom: 12px\" align=\"center\">\n            <n-tag v-if=\"benchmarkUsedRemote\" type=\"info\" size=\"small\">\n              {{ remoteApi.model || '远程API' }}\n            </n-tag>\n            <n-tag v-else type=\"success\" size=\"small\">本地模型</n-tag>\n            <n-tag v-if=\"benchmarkResult.modelTelemetry?.hardware?.isGpuMode\" type=\"success\" size=\"small\">\n              🚀 {{ benchmarkResult.modelTelemetry.hardware.executionDevice }}\n            </n-tag>\n            <n-tag v-else-if=\"benchmarkResult.modelTelemetry?.hardware\" type=\"warning\" size=\"small\">\n              💻 {{ benchmarkResult.modelTelemetry.hardware.executionDevice }}\n            </n-tag>\n            <n-tag v-if=\"benchmarkResult.modelTelemetry?.hardware?.offloadDescription\" type=\"info\" size=\"small\">\n              {{ benchmarkResult.modelTelemetry.hardware.offloadDescription }}\n            </n-tag>\n            <n-tag v-if=\"benchmarkResult.modelTelemetry?.configuration?.quantization\" size=\"small\">\n              {{ benchmarkResult.modelTelemetry.configuration.quantization }}\n            </n-tag>\n          </n-space>\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"分段耗时\" :value=\"benchmarkResult.chunkingMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"Token 计数\" :value=\"benchmarkResult.tokenCountMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"向量化耗时\" :value=\"benchmarkResult.embeddingMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"总耗时\" :value=\"benchmarkResult.totalMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n          </n-grid>\n          <n-divider />\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"分段数\" :value=\"benchmarkResult.chunkCount\" /></n-gi>\n            <n-gi><n-statistic label=\"向量数\" :value=\"benchmarkResult.vectorCount\" /></n-gi>\n            <n-gi><n-statistic label=\"向量维度\" :value=\"benchmarkResult.vectorDimension\" /></n-gi>\n            <n-gi><n-statistic label=\"预估内存\" :value=\"benchmarkResult.estimatedMemoryMB.toFixed(1)\" /><n-text depth=\"3\">MB</n-text></n-gi>\n          </n-grid>\n          <n-divider />\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"Tokens/s\" :value=\"benchmarkResult.tokensPerSecond.toFixed(0)\" /></n-gi>\n            <n-gi><n-statistic label=\"Chars/s\" :value=\"benchmarkResult.charsPerSecond.toFixed(0)\" /></n-gi>\n            <n-gi><n-statistic label=\"Chunks/s\" :value=\"benchmarkResult.chunksPerSecond.toFixed(2)\" /></n-gi>\n            <n-gi><n-statistic label=\"Vectors/s\" :value=\"benchmarkResult.vectorsPerSecond.toFixed(2)\" /></n-gi>\n          </n-grid>\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Batch & Memory -->\n      <n-tab-pane name=\"optimization\" tab=\"批量与内存\">\n        <n-grid :cols=\"2\" :x-gap=\"20\">\n          <n-gi>\n            <n-card title=\"批量大小优化\">\n              <n-space vertical>\n                <n-form-item label=\"文本长度\">\n                  <n-input-number v-model:value=\"batchTextLength\" :min=\"5000\" :max=\"1000000\" :step=\"10000\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-button type=\"primary\" :loading=\"batchLoading\" @click=\"runBatchTest\">运行测试</n-button>\n              </n-space>\n            </n-card>\n            <n-card v-if=\"batchResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n              <n-data-table :columns=\"batchColumns\" :data=\"batchResult.results\" :bordered=\"false\" size=\"small\" />\n            </n-card>\n          </n-gi>\n          <n-gi>\n            <n-card title=\"内存测试\">\n              <n-space vertical>\n                <n-grid :cols=\"2\" :x-gap=\"12\">\n                  <n-gi>\n                    <n-form-item label=\"向量数量\">\n                      <n-input-number v-model:value=\"memVectorCount\" :min=\"1\" :max=\"100000\" style=\"width: 100%\" />\n                    </n-form-item>\n                  </n-gi>\n                  <n-gi>\n                    <n-form-item label=\"向量维度\">\n                      <n-input-number v-model:value=\"memDimension\" :min=\"1\" :max=\"4096\" style=\"width: 100%\" />\n                    </n-form-item>\n                  </n-gi>\n                </n-grid>\n                <n-button type=\"primary\" :loading=\"memLoading\" @click=\"runMemoryTest\">运行测试</n-button>\n              </n-space>\n            </n-card>\n            <n-card v-if=\"memResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n              <n-descriptions :column=\"2\">\n                <n-descriptions-item label=\"内存占用\">{{ memResult.memoryMB.toFixed(2) }} MB</n-descriptions-item>\n                <n-descriptions-item label=\"分配耗时\">{{ memResult.allocationTimeMs }} ms</n-descriptions-item>\n              </n-descriptions>\n            </n-card>\n          </n-gi>\n        </n-grid>\n      </n-tab-pane>\n\n      <!-- OpenAI-compatible API Test -->\n      <n-tab-pane name=\"api-compare\" tab=\"API 对比测试\">\n        <n-card title=\"API 配置\">\n          <n-space vertical>\n            <n-grid :cols=\"4\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"Base URL\">\n                  <n-input v-model:value=\"remoteApi.baseUrl\" placeholder=\"http://localhost:11434\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"remoteApi.apiKey\" type=\"password\" placeholder=\"留空则不发送\" show-password-on=\"click\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"remoteApi.model\" placeholder=\"bge-m3\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"批量大小\">\n                  <n-input-number v-model:value=\"remoteApi.batchSize\" :min=\"1\" :max=\"128\" style=\"width: 100%\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"apiTesting\" :disabled=\"!remoteApi.baseUrl\" @click=\"testApiConnection\">\n                测试连接\n              </n-button>\n              <n-tag v-if=\"apiTestResult\" :type=\"apiTestResult.success ? 'success' : 'error'\">\n                {{ apiTestResult.message }}\n                <template v-if=\"apiTestResult.latency\"> ({{ apiTestResult.latency }}ms)</template>\n              </n-tag>\n            </n-space>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"remoteLoading\" :disabled=\"!remoteApi.baseUrl\" @click=\"runRemoteTest\">\n                运行全部测试（100 项）\n              </n-button>\n              <n-button :loading=\"remoteLoading && remoteActiveCategory !== null\" :disabled=\"!remoteApi.baseUrl\" @click=\"runRemoteAllCategories\">\n                按分类依次测试\n              </n-button>\n            </n-space>\n            <n-text depth=\"3\">支持任意 OpenAI 兼容接口：Ollama、vLLM、Xinference、OpenAI 等。配置自动保存。</n-text>\n          </n-space>\n        </n-card>\n\n        <!-- Category Buttons -->\n        <n-card title=\"分类测试\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"cat in semanticCategories\"\n              :key=\"cat.label\"\n              :loading=\"remoteLoading && remoteActiveCategory === cat.label\"\n              size=\"small\"\n              :type=\"remoteActiveCategory === cat.label ? 'primary' : 'default'\"\n              @click=\"runRemoteCategoryTest(cat)\"\n            >\n              {{ cat.label }}（{{ cat.cases.length }}）\n            </n-button>\n          </n-space>\n        </n-card>\n\n        <!-- Results -->\n        <n-card v-if=\"remoteResults.length > 0\" title=\"测试结果\" style=\"margin-top: 12px\">\n          <template #header-extra>\n            <n-tag type=\"info\" size=\"small\">{{ remoteApi.model }}</n-tag>\n          </template>\n          <!-- Summary -->\n          <n-space style=\"margin-bottom: 16px\">\n            <n-tag type=\"success\" size=\"large\">\n              通过: {{ remoteOverallPassRate.passed }} / {{ remoteOverallPassRate.total }}（{{ remoteOverallPassRate.rate }}）\n            </n-tag>\n            <n-tag v-if=\"remoteMae !== null\" size=\"large\">\n              MAE: {{ remoteMae.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"remoteRmse !== null\" size=\"large\">\n              RMSE: {{ remoteRmse.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"remoteCorrelation !== null\" size=\"large\">\n              相关性: {{ remoteCorrelation.toFixed(3) }}\n            </n-tag>\n            <n-tag size=\"large\">\n              耗时: {{ remoteTotalTime }}ms\n            </n-tag>\n          </n-space>\n\n          <!-- Category Stats -->\n          <n-data-table\n            :columns=\"remoteCategoryColumns\"\n            :data=\"remoteCategoryStats\"\n            :bordered=\"false\"\n            size=\"small\"\n            style=\"margin-bottom: 16px\"\n          />\n\n          <!-- Detail Table -->\n          <n-data-table\n            :columns=\"remoteDetailColumns\"\n            :data=\"remoteResults\"\n            :bordered=\"false\"\n            size=\"small\"\n            :max-height=\"400\"\n            :scroll-x=\"800\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- API Functionality Test -->\n      <n-tab-pane name=\"api-test\" tab=\"API 功能测试\">\n        <n-card title=\"第三方 OpenAI 兼容 API 测试\">\n          <n-space vertical>\n            <n-text depth=\"3\">测试任意 OpenAI 兼容接口的功能和性能，验证向量维度、响应时间、批量处理能力等。</n-text>\n\n            <n-grid :cols=\"4\" :x-gap=\"16\">\n              <n-gi>\n                <n-form-item label=\"Base URL\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.baseUrl\" placeholder=\"http://localhost:11434\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"API Key\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.apiKey\" type=\"password\" placeholder=\"可选\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"模型名称\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.model\" placeholder=\"bge-m3\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"测试文本\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.testText\" placeholder=\"输入测试文本\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n\n            <n-space>\n              <n-button type=\"primary\" :loading=\"apiFuncTesting\" @click=\"runApiFunctionTest\">\n                运行功能测试\n              </n-button>\n              <n-button :loading=\"apiFuncTesting\" @click=\"runApiBatchTest\">\n                批量性能测试\n              </n-button>\n              <n-button @click=\"loadPresetConfig('lmstudio')\">LM Studio</n-button>\n              <n-button @click=\"loadPresetConfig('ollama')\">Ollama</n-button>\n              <n-button @click=\"loadPresetConfig('openai')\">OpenAI</n-button>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"apiFuncResult\" title=\"测试结果\" style=\"margin-top: 12px\">\n          <n-descriptions :column=\"3\" label-placement=\"left\">\n            <n-descriptions-item label=\"状态\">\n              <n-tag :type=\"apiFuncResult.success ? 'success' : 'error'\">\n                {{ apiFuncResult.success ? '成功' : '失败' }}\n              </n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"向量维度\">{{ apiFuncResult.dimension || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"响应时间\">{{ apiFuncResult.latencyMs || '-' }} ms</n-descriptions-item>\n            <n-descriptions-item label=\"模型\">{{ apiFuncResult.model || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"Token 数量\">{{ apiFuncResult.tokenCount || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"批量支持\">{{ apiFuncResult.batchSupported ? '是' : '未知' }}</n-descriptions-item>\n          </n-descriptions>\n\n          <n-divider>向量采样（前10维）</n-divider>\n          <n-code v-if=\"apiFuncResult.embeddingSample\" :code=\"apiFuncResult.embeddingSample\" language=\"json\" />\n\n          <n-divider v-if=\"apiFuncResult.batchResults\">批量测试结果</n-divider>\n          <n-data-table v-if=\"apiFuncResult.batchResults\" :columns=\"apiBatchColumns\" :data=\"apiFuncResult.batchResults\" :bordered=\"false\" size=\"small\" />\n        </n-card>\n\n        <n-card title=\"连接历史\" size=\"small\" style=\"margin-top: 12px\">\n          <n-data-table :columns=\"apiHistoryColumns\" :data=\"apiHistory\" :bordered=\"false\" size=\"small\" :max-height=\"200\" />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Rerank Model Test -->\n          </n-tabs>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"rerank\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600; font-size: 15px\">重排模型 (Rerank)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"warning\">交叉编码 / 深度精排 / 排序对抗</n-tag>\n          </n-space>\n        </template>\n\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- 重排模型运行状态与硬件加速 (Telemetry) -->\n    <n-card size=\"small\" style=\"margin-bottom: 8px\">\n      <template #header>\n        <n-space align=\"center\" :size=\"10\">\n          <span style=\"font-weight: 600\">重排模型运行状态与硬件加速</span>\n          <template v-if=\"rerankTelemetry\">\n            <n-tag\n              :type=\"rerankTelemetry.stateMachine.state === 2 ? 'success' : (rerankTelemetry.stateMachine.state === 3 ? 'info' : (rerankTelemetry.stateMachine.state === 5 ? 'error' : 'warning'))\"\n              size=\"small\"\n              round\n            >\n              {{ rerankTelemetry.stateMachine.stateName }} ({{ rerankTelemetry.stateMachine.stateDescription }})\n            </n-tag>\n            <n-tag\n              :type=\"rerankTelemetry.hardware.isGpuMode ? 'success' : 'warning'\"\n              size=\"small\"\n              round\n            >\n              <template #icon>\n                <span>{{ rerankTelemetry.hardware.isGpuMode ? '🚀' : '💻' }}</span>\n              </template>\n              {{ rerankTelemetry.hardware.isGpuMode ? 'GPU 硬件加速' : 'CPU 计算模式' }}\n            </n-tag>\n            <n-tag v-if=\"rerankTelemetry.hardware.isGpuMode && rerankTelemetry.hardware.isFullOffload\" type=\"info\" size=\"small\">\n              全量 Offload\n            </n-tag>\n            <n-tag v-if=\"rerankTelemetry?.configuration?.quantization\" size=\"small\" :bordered=\"false\">\n              {{ rerankTelemetry.configuration.quantization }}\n            </n-tag>\n          </template>\n        </n-space>\n      </template>\n      <template #header-extra>\n        <n-space align=\"center\" :size=\"8\">\n          <n-text depth=\"3\" style=\"font-size: 11px\" v-if=\"rerankTelemetry?.timestamp\">\n            更新于: {{ new Date(rerankTelemetry.timestamp).toLocaleTimeString() }}\n          </n-text>\n          <n-button text size=\"small\" :loading=\"rerankTelemetryLoading\" @click=\"loadRerankTelemetry\">\n            刷新状态\n          </n-button>\n        </n-space>\n      </template>\n\n      <n-spin :show=\"rerankTelemetryLoading\" style=\"min-height: 48px\">\n        <div v-if=\"rerankTelemetry\">\n          <n-alert v-if=\"rerankTelemetry.stateMachine.lastError\" type=\"error\" style=\"margin-bottom: 12px\" title=\"重排模型运行异常\">\n            {{ rerankTelemetry.stateMachine.lastError }}\n          </n-alert>\n\n          <!-- 遥测参数网格 -->\n          <n-grid :cols=\"6\" :x-gap=\"12\" :y-gap=\"8\">\n            <n-gi>\n              <n-statistic label=\"活跃重排模型\">\n                <template #default>\n                  <n-text style=\"font-size: 12px; font-weight: bold; word-break: break-all\">\n                    {{ rerankTelemetry.configuration.displayName || rerankTelemetry.configuration.modelName || '—' }}\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"执行设备 / 后端\">\n                <template #default>\n                  <n-text style=\"font-size: 12px; word-break: break-all\">\n                    {{ rerankTelemetry.hardware.executionDevice || rerankTelemetry.configuration.backendDisplayName }}\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"GPU 卸载状态\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">\n                    {{ rerankTelemetry.hardware.offloadDescription || (rerankTelemetry.hardware.isGpuMode ? '启用' : '无') }}\n                  </n-text>\n                  <n-text v-if=\"rerankTelemetry.hardware.totalVramGb\" depth=\"3\" style=\"font-size: 10px; display: block\">\n                    显存容量: {{ rerankTelemetry.hardware.totalVramGb }} GB\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"驻留策略\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">\n                    {{ rerankTelemetry.configuration.residentStrategy || '自动休眠释放' }}\n                  </n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最大上下文 (Context)\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">{{ rerankTelemetry.configuration.contextSize }} tokens</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 10px; display: block\">BatchSize: {{ rerankTelemetry.configuration.batchSize }}</n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"架构与规格\">\n                <template #default>\n                  <n-text style=\"font-size: 12px\">{{ rerankTelemetry.configuration.poolingType || 'CrossEncoder' }}</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 10px; display: block\">体积: {{ rerankTelemetry.configuration.modelSizeFriendly }}</n-text>\n                </template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n        </div>\n        <n-text v-else-if=\"!rerankTelemetryLoading\" depth=\"3\">暂无重排模型运行状态数据，点击「刷新状态」获取</n-text>\n      </n-spin>\n    </n-card>\n\n          \n\n          <!-- Rerank Sub-tabs -->\n          <n-tabs type=\"line\" animated v-model:value=\"activeRerankSubTab\">\n            <n-tab-pane name=\"quality\" tab=\"语义与质量精排测试\">\n              <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n                <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试重排模型的文档排序能力：给定查询和文档列表，模型返回按相关性排序的结果。支持 NDCG、MRR、MAP 等评估指标。</n-text>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"rerankLoading\" @click=\"runAllRerankPresets\">\n                运行全部预设测试\n              </n-button>\n              <n-button @click=\"showCustomRerank = true\">\n                自定义测试\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForRerank\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForRerank && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <!-- Preset Test Buttons -->\n        <n-card title=\"预设测试套件\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"preset in rerankPresets\"\n              :key=\"preset.name\"\n              :loading=\"rerankLoading && activeRerankPreset === preset.name\"\n              size=\"small\"\n              :type=\"activeRerankPreset === preset.name ? 'primary' : 'default'\"\n              @click=\"runRerankPreset(preset.name)\"\n            >\n              {{ preset.description }}（{{ preset.documentCount }} 文档）\n            </n-button>\n          </n-space>\n        </n-card>\n\n        <!-- Custom Rerank Test Dialog -->\n        <n-modal v-model:show=\"showCustomRerank\" preset=\"dialog\" title=\"自定义重排测试\" style=\"width: 60vw\">\n          <n-space vertical>\n            <n-alert v-if=\"customRerankError\" type=\"error\" style=\"margin-bottom: 8px\">{{ customRerankError }}</n-alert>\n            <n-form-item label=\"查询文本\">\n              <n-input v-model:value=\"rerankQuery\" placeholder=\"输入查询文本\" />\n            </n-form-item>\n            <n-form-item label=\"模型名称\">\n              <n-input v-model:value=\"rerankModelName\" placeholder=\"留空使用默认\" />\n            </n-form-item>\n            <n-form-item label=\"文档列表 (JSON)\">\n              <n-input\n                v-model:value=\"rerankDocumentsJson\"\n                type=\"textarea\"\n                :rows=\"10\"\n                placeholder='[{\"id\":\"1\",\"text\":\"文档内容\",\"expectedRelevance\":0.9}]'\n                style=\"font-family: monospace\"\n              />\n            </n-form-item>\n            <n-text depth=\"3\" style=\"font-size: 12px\">\n              格式: [{\"id\": \"1\", \"text\": \"文档内容\", \"expectedRelevance\": 0.9}]，expectedRelevance 为 0-1 的期望相关性分数\n            </n-text>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"rerankLoading\" @click=\"runCustomRerankTest\">\n                运行测试\n              </n-button>\n              <n-button @click=\"loadSampleRerankDocuments\">\n                加载示例数据\n              </n-button>\n            </n-space>\n          </n-space>\n        </n-modal>\n\n        <!-- Rerank Statistics -->\n        <n-card v-if=\"rerankStatistics\" title=\"测试统计\" style=\"margin-top: 16px\">\n          <n-grid :cols=\"6\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"总测试数\" :value=\"rerankStatistics.totalTests\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 NDCG\" :value=\"rerankStatistics.byModel[0].avgNdcg.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MRR\" :value=\"rerankStatistics.byModel[0].avgMrr.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MAP\" :value=\"rerankStatistics.byModel[0].avgMap.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均耗时\" :value=\"rerankStatistics.byModel[0].avgQueryMs.toFixed(0)\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MAE\" :value=\"rerankStatistics.byModel[0].avgMae.toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n        </n-card>\n\n        <!-- 重排模型四维精排能力画像 -->\n        <n-card v-if=\"rerankAllPresetsResults.length > 0\" title=\"重排模型四维精排能力画像\" size=\"small\" style=\"margin-top: 16px; background: rgba(128, 128, 128, 0.03)\">\n          <template #header-extra>\n            <n-space align=\"center\" :size=\"8\">\n              <span style=\"font-size: 13px; color: #888\">重排综合效能指数:</span>\n              <n-tag :type=\"parseFloat(rerankCompositeIndex) >= 80 ? 'success' : (parseFloat(rerankCompositeIndex) >= 60 ? 'info' : 'warning')\" size=\"medium\" round>\n                <span style=\"font-weight: bold; font-size: 15px\">{{ rerankCompositeIndex }}</span> / 100\n              </n-tag>\n            </n-space>\n          </template>\n\n          <n-grid :cols=\"4\" :x-gap=\"12\" :y-gap=\"12\" style=\"margin-bottom: 12px\">\n            <n-gi v-for=\"domain in rerankDomainStats\" :key=\"domain.key\">\n              <n-card size=\"small\" :bordered=\"true\" style=\"height: 100%\">\n                <n-space justify=\"space-between\" align=\"center\" style=\"margin-bottom: 8px\">\n                  <n-space align=\"center\" :size=\"6\">\n                    <span style=\"font-size: 16px\">{{ domain.icon }}</span>\n                    <span style=\"font-weight: 600; font-size: 13px\">{{ domain.name }}</span>\n                  </n-space>\n                  <n-tag :type=\"domain.tagType\" size=\"tiny\" round>{{ domain.rating }}</n-tag>\n                </n-space>\n                <n-progress\n                  type=\"line\"\n                  :percentage=\"parseFloat(domain.score)\"\n                  :status=\"parseFloat(domain.score) >= 80 ? 'success' : (parseFloat(domain.score) >= 60 ? 'info' : 'warning')\"\n                  :show-indicator=\"false\"\n                  style=\"margin-bottom: 8px\"\n                />\n                <n-space justify=\"space-between\" align=\"center\" style=\"font-size: 12px\">\n                  <span style=\"color: #888\">NDCG: <strong style=\"color: var(--n-text-color)\">{{ domain.avgNdcg }}</strong></span>\n                  <span style=\"color: #888\">MRR: <strong style=\"color: var(--n-text-color)\">{{ domain.avgMrr }}</strong></span>\n                  <span style=\"color: #888\">耗时: {{ domain.avgLatency }}ms</span>\n                </n-space>\n                <div style=\"font-size: 11px; color: #888; margin-top: 6px; line-height: 1.4\">\n                  {{ domain.description }}\n                </div>\n              </n-card>\n            </n-gi>\n          </n-grid>\n\n          <n-alert type=\"success\" :show-icon=\"true\">\n            <template #header>💡 重排模型协同价值诊断</template>\n            <div style=\"font-size: 12px; line-height: 1.6\">\n              Cross-Encoder 架构对文档进行逐字交叉注意力打分，能够识别<strong>词汇高重合但逻辑相反</strong>的硬负例，并精准定位<strong>长段落中的核心条款</strong>。\n              搭配向量检索使用时，建议粗排召回 Top-20 ~ Top-50，由重排模型精排输出 Top-3 ~ Top-5，达成准确率与延迟的最优平衡。\n            </div>\n          </n-alert>\n        </n-card>\n\n        <!-- 全部预设多套件横向对比看板 -->\n        <n-card v-if=\"rerankAllPresetsResults.length > 0\" id=\"rerank-board-card\" title=\"预设套件横向对比看板\" style=\"margin-top: 16px\">\n          <template #header-extra>\n            <n-space align=\"center\" :size=\"8\">\n              <n-tag v-if=\"activeRerankPresetKey\" type=\"info\" size=\"small\" round>\n                当前查看: {{ rerankAllPresetsResults.find(p => p.name === activeRerankPresetKey)?.description || activeRerankPresetKey }}\n              </n-tag>\n              <n-tag type=\"success\" size=\"small\" round>共评测 {{ rerankAllPresetsResults.length }} 组测试套件</n-tag>\n            </n-space>\n          </template>\n          <n-data-table\n            :columns=\"rerankPresetSummaryColumns\"\n            :data=\"rerankAllPresetsResults\"\n            :row-props=\"rerankRowProps\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n\n        <n-card v-if=\"rerankResult\" id=\"rerank-detail-card\" :title=\"activeRerankPresetTitle\" style=\"margin-top: 16px\">\n          <template #header-extra>\n            <n-space align=\"center\" :size=\"8\">\n              <template v-if=\"rerankAllPresetsResults.length > 1\">\n                <n-tag type=\"default\" size=\"small\" round>\n                  {{ (activeRerankPresetIndex >= 0 ? activeRerankPresetIndex + 1 : 1) }} / {{ rerankAllPresetsResults.length }} 套件\n                </n-tag>\n                <n-button-group size=\"small\">\n                  <n-button\n                    size=\"small\"\n                    secondary\n                    :disabled=\"activeRerankPresetIndex <= 0\"\n                    @click=\"prevRerankSuite\"\n                  >\n                    ◀ 上一套件\n                  </n-button>\n                  <n-button\n                    size=\"small\"\n                    secondary\n                    :disabled=\"activeRerankPresetIndex < 0 || activeRerankPresetIndex >= rerankAllPresetsResults.length - 1\"\n                    @click=\"nextRerankSuite\"\n                  >\n                    下一套件 ▶\n                  </n-button>\n                </n-button-group>\n                <n-select\n                  v-model:value=\"activeRerankPresetKey\"\n                  :options=\"rerankPresetOptions\"\n                  size=\"small\"\n                  style=\"width: 220px\"\n                  @update:value=\"(val) => selectRerankSuite(val)\"\n                />\n                <n-button size=\"small\" text @click=\"scrollToRerankBoard\" title=\"回到预设套件横向对比看板\">\n                  ⬆ 看板\n                </n-button>\n                <n-divider vertical style=\"margin: 0 4px\" />\n              </template>\n              <n-button size=\"small\" ghost type=\"primary\" @click=\"exportRerankReport\">导出评测报告</n-button>\n              <n-tag type=\"info\" size=\"small\" round>{{ rerankResult.modelName || 'default' }}</n-tag>\n              <n-tag v-if=\"rerankEvaluation\" :type=\"rerankEvaluation.gradeType\" size=\"small\" round>\n                {{ rerankEvaluation.grade }} 级综合评估\n              </n-tag>\n            </n-space>\n          </template>\n\n          <!-- 最简数据紧凑指示条 (Compact Summary Bar) -->\n          <div style=\"background: rgba(128, 128, 128, 0.06); border: 1px solid rgba(128, 128, 128, 0.12); border-radius: 8px; padding: 8px 14px; margin-bottom: 12px; display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 10px;\">\n            <n-space align=\"center\" :size=\"10\" style=\"flex-wrap: wrap\">\n              <n-tag v-if=\"rerankEvaluation\" :type=\"rerankEvaluation.gradeType\" size=\"small\" round style=\"font-weight: 600\">\n                <strong style=\"margin-right: 4px\">{{ rerankEvaluation.grade }} 级</strong>{{ rerankEvaluation.title }}\n              </n-tag>\n              <n-divider vertical style=\"margin: 0 2px\" />\n              <n-space align=\"center\" :size=\"12\" style=\"font-size: 13px\">\n                <span><n-text depth=\"3\">NDCG: </n-text><strong>{{ (rerankResult.ndcg || 0).toFixed(3) }}</strong></span>\n                <span><n-text depth=\"3\">MRR: </n-text><strong>{{ (rerankResult.mrr || 0).toFixed(3) }}</strong></span>\n                <span><n-text depth=\"3\">MAP: </n-text><strong>{{ (rerankResult.map || 0).toFixed(3) }}</strong></span>\n                <span><n-text depth=\"3\">准确度: </n-text><strong>{{ (rerankResult.rankingAccuracy || 0).toFixed(3) }}</strong></span>\n                <span><n-text depth=\"3\">MAE: </n-text><strong>{{ (rerankResult.meanAbsoluteError || 0).toFixed(3) }}</strong></span>\n                <span><n-text depth=\"3\">耗时: </n-text><strong>{{ rerankResult.queryMs || 0 }}ms</strong></span>\n              </n-space>\n            </n-space>\n            <n-button text size=\"small\" type=\"primary\" @click=\"showRerankFullMetrics = !showRerankFullMetrics\" style=\"font-weight: 500\">\n              {{ showRerankFullMetrics ? '收起详细指标与诊断 ▴' : '展开详细指标与诊断 ▾' }}\n            </n-button>\n          </div>\n\n          <!-- 折叠详细指标与诊断区域 -->\n          <n-collapse-transition :show=\"showRerankFullMetrics\">\n            <div style=\"padding-top: 4px; margin-bottom: 12px\">\n              <!-- 诊断与落地引导面板 -->\n          <n-alert v-if=\"rerankEvaluation\" :type=\"rerankEvaluation.gradeType\" style=\"margin-bottom: 16px\" :title=\"rerankEvaluation.title\">\n            <div style=\"margin-top: 6px; line-height: 1.8\">\n              <ul style=\"margin: 0; padding-left: 18px\">\n                <li v-for=\"(tip, i) in rerankEvaluation.tips\" :key=\"i\">{{ tip }}</li>\n              </ul>\n              <n-divider style=\"margin: 8px 0\" />\n              <n-text depth=\"2\"><strong>💡 生产落地与 Top-K 截断建议：</strong>{{ rerankEvaluation.cutoffTip }}</n-text>\n            </div>\n          </n-alert>\n\n          <!-- 参数说明对照折叠面板 -->\n          <n-collapse style=\"margin-bottom: 16px\">\n            <n-collapse-item title=\"📘 重排核心性能参数对照表与指标解读指南\" name=\"rerank-guide\">\n              <n-table size=\"small\" :single-line=\"false\" style=\"margin-top: 8px\">\n                <thead>\n                  <tr><th>指标</th><th>全称与计算逻辑</th><th>标准范围</th><th>在 RAG 重排中的工程意义</th></tr>\n                </thead>\n                <tbody>\n                  <tr v-for=\"m in rerankMetricsGuide\" :key=\"m.key\">\n                    <td><strong>{{ m.key }}</strong></td>\n                    <td>{{ m.name }}<br/><n-text depth=\"3\" style=\"font-size: 11px\">{{ m.formula }}</n-text></td>\n                    <td><n-tag size=\"small\" :bordered=\"false\">{{ m.benchmark }}</n-tag></td>\n                    <td>{{ m.desc }}</td>\n                  </tr>\n                </tbody>\n              </n-table>\n            </n-collapse-item>\n          </n-collapse>\n          <!-- Metrics -->\n          <n-grid :cols=\"5\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n            <n-gi>\n              <n-statistic label=\"NDCG\" :value=\"(rerankResult.ndcg || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MRR\" :value=\"(rerankResult.mrr || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MAP\" :value=\"(rerankResult.map || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"排序准确度\" :value=\"(rerankResult.rankingAccuracy || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MAE\" :value=\"(rerankResult.meanAbsoluteError || 0).toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"2\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"查询耗时\" :value=\"rerankResult.queryMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"模型\" :value=\"rerankResult.modelName || '-'\" />\n            </n-gi>\n          </n-grid>\n            </div>\n          </n-collapse-transition>\n\n          <!-- Document Ranking -->\n          <n-divider>文档排序</n-divider>\n          <n-data-table\n            :columns=\"rerankDocColumns\"\n            :data=\"rerankResult.documents || []\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n              </n-space>\n            </n-tab-pane>\n\n            <n-tab-pane name=\"benchmark\" tab=\"性能基准与并发压测\">\n              <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n                <!-- Performance Benchmark -->\n        <n-card title=\"性能基准测试\" size=\"small\" style=\"margin-top: 16px\">\n          <n-space vertical>\n            <n-text depth=\"3\">测试重排模型的吞吐量和延迟分布</n-text>\n            <n-grid :cols=\"4\" :x-gap=\"16\">\n              <n-gi>\n                <n-form-item label=\"并发数\">\n                  <n-input-number v-model:value=\"benchmarkConcurrency\" :min=\"1\" :max=\"32\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"总请求数\">\n                  <n-input-number v-model:value=\"benchmarkTotalRequests\" :min=\"10\" :max=\"1000\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"每请求文档数\">\n                  <n-input-number v-model:value=\"benchmarkDocCount\" :min=\"1\" :max=\"20\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"操作\">\n                  <n-button type=\"primary\" :loading=\"rerankBenchmarkLoading\" @click=\"runRerankBenchmark\">\n                    开始测试\n                  </n-button>\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"rerankBenchmarkResult\" title=\"性能测试结果\" style=\"margin-top: 12px\">\n          <n-grid :cols=\"5\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"QPS\" :value=\"rerankBenchmarkResult.qps.toFixed(2)\">\n                <template #suffix>req/s</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"平均延迟\" :value=\"rerankBenchmarkResult.avgLatencyMs.toFixed(1)\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P50 延迟\" :value=\"rerankBenchmarkResult.p50LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P95 延迟\" :value=\"rerankBenchmarkResult.p95LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P99 延迟\" :value=\"rerankBenchmarkResult.p99LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"4\" :x-gap=\"16\" style=\"margin-top: 12px\">\n            <n-gi>\n              <n-statistic label=\"总耗时\" :value=\"rerankBenchmarkResult.totalMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"成功/失败\" :value=\"`${rerankBenchmarkResult.successCount}/${rerankBenchmarkResult.failCount}`\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最小延迟\" :value=\"rerankBenchmarkResult.minLatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最大延迟\" :value=\"rerankBenchmarkResult.maxLatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n          <n-alert v-if=\"rerankBenchmarkResult.errors.length > 0\" type=\"warning\" style=\"margin-top: 12px\" title=\"错误信息\">\n            <ul style=\"margin: 0; padding-left: 20px\">\n              <li v-for=\"(err, i) in rerankBenchmarkResult.errors\" :key=\"i\">{{ err }}</li>\n            </ul>\n          </n-alert>\n        </n-card>\n              </n-space>\n            </n-tab-pane>\n          </n-tabs>\n\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"chat\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600; font-size: 15px\">聊天模型 (Chat)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"success\">本地 GGUF / 推理生成 / TTFT&TPS 测速</n-tag>\n          </n-space>\n        </template>\n\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- 聊天模型运行状态与硬件加速 (Telemetry) -->\n          <n-card size=\"small\" style=\"margin-bottom: 8px\">\n            <template #header>\n              <n-space align=\"center\" :size=\"10\">\n                <span style=\"font-weight: 600\">聊天模型运行状态与硬件加速</span>\n                <template v-if=\"chatTelemetry\">\n                  <n-tag\n                    :type=\"chatTelemetry.state === 2 ? 'success' : (chatTelemetry.state === 3 ? 'info' : (chatTelemetry.state === 5 ? 'error' : 'warning'))\"\n                    size=\"small\"\n                    round\n                  >\n                    {{ chatTelemetry.stateName || '就绪' }} ({{ chatTelemetry.stateDescription || '就绪' }})\n                  </n-tag>\n                  <n-tag\n                    :type=\"chatTelemetry.isGpuMode ? 'success' : 'warning'\"\n                    size=\"small\"\n                    round\n                  >\n                    <template #icon>\n                      <span>{{ chatTelemetry.isGpuMode ? '🚀' : '💻' }}</span>\n                    </template>\n                    {{ chatTelemetry.isGpuMode ? 'GPU 硬件加速' : 'CPU 计算模式' }}\n                  </n-tag>\n                  <n-tag v-if=\"chatTelemetry.quantization\" size=\"small\" :bordered=\"false\">\n                    {{ chatTelemetry.quantization }}\n                  </n-tag>\n                  <n-tag v-if=\"chatTelemetry.modelFormat\" size=\"small\" :bordered=\"false\" type=\"info\">\n                    {{ chatTelemetry.modelFormat.toUpperCase() }}\n                  </n-tag>\n                </template>\n              </n-space>\n            </template>\n            <template #header-extra>\n              <n-space align=\"center\" :size=\"8\">\n                <n-button text size=\"small\" :loading=\"chatTelemetryLoading\" @click=\"loadChatTelemetry\">\n                  刷新状态\n                </n-button>\n              </n-space>\n            </template>\n\n            <n-spin :show=\"chatTelemetryLoading\" style=\"min-height: 48px\">\n              <div v-if=\"chatTelemetry\">\n                <n-alert v-if=\"chatTelemetry.lastError\" type=\"error\" style=\"margin-bottom: 12px\" title=\"聊天模型运行异常\">\n                  {{ chatTelemetry.lastError }}\n                </n-alert>\n\n                <!-- 遥测参数网格 -->\n                <n-grid :cols=\"6\" :x-gap=\"12\" :y-gap=\"8\">\n                  <n-gi>\n                    <n-statistic label=\"活跃聊天模型\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px; font-weight: bold; word-break: break-all\">\n                          {{ chatTelemetry.displayName || chatTelemetry.modelName || '—' }}\n                        </n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                  <n-gi>\n                    <n-statistic label=\"执行设备 / 格式\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px; word-break: break-all\">\n                          {{ chatTelemetry.executionDevice || 'CPU' }} ({{ chatTelemetry.modelFormat || 'gguf' }})\n                        </n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                  <n-gi>\n                    <n-statistic label=\"GPU 卸载状态\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px\">\n                          {{ chatTelemetry.offloadDescription || (chatTelemetry.isGpuMode ? 'GPU 加速' : 'CPU 纯算') }}\n                        </n-text>\n                        <n-text v-if=\"chatTelemetry.totalVramGb\" depth=\"3\" style=\"font-size: 10px; display: block\">\n                          显存容量: {{ chatTelemetry.totalVramGb }} GB\n                        </n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                  <n-gi>\n                    <n-statistic label=\"当前激活变体\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px; word-break: break-all\">\n                          {{ chatTelemetry.activeVariant || '默认变体' }}\n                        </n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                  <n-gi>\n                    <n-statistic label=\"最大上下文 (Context)\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px\">{{ chatTelemetry.maxSequenceLength || 4096 }} tokens</n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                  <n-gi>\n                    <n-statistic label=\"量化精度\">\n                      <template #default>\n                        <n-text style=\"font-size: 12px\">{{ chatTelemetry.quantization || 'Q4_K_M' }}\n                        </n-text>\n                      </template>\n                    </n-statistic>\n                  </n-gi>\n                </n-grid>\n              </div>\n              <n-text v-else-if=\"!chatTelemetryLoading\" depth=\"3\">暂无聊天模型运行状态数据，点击「刷新状态」获取</n-text>\n            </n-spin>\n          </n-card>\n\n          <!-- 聊天模型二级分类：全维评测矩阵 vs 实时流式测速 -->\n          <n-tabs type=\"line\" animated v-model:value=\"activeChatSubTab\">\n            <n-tab-pane name=\"matrix\" tab=\"全维能力评测与工具调用验收\">\n              <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n                <!-- 操作与概览卡片 -->\n                <n-card title=\"端侧大模型工具调用与质量评测矩阵\">\n                  <n-space vertical :size=\"10\">\n                    <n-text depth=\"3\">\n                      针对端侧大语言模型（特别是 &lt;1B 小模型）的显式指令分发、自然语言搜索意图抽取、复合工具名前缀容错、精确关键词检索、空召回保真抗幻觉及日常闲聊拒调等 7 大能力维度进行自动化验证与策略判定。\n                    </n-text>\n                    <n-space align=\"center\">\n                      <n-button type=\"primary\" :loading=\"chatEvalRunning\" @click=\"runAllChatEvalCases\">\n                        运行全部评测（12 项）\n                      </n-button>\n                      <n-button v-if=\"chatEvalRunning\" type=\"error\" ghost @click=\"cancelChatEval\">\n                        终止评测\n                      </n-button>\n                      <n-divider vertical />\n                      <n-button secondary @click=\"exportChatEvalReport\">\n                        导出评测报告 (.md)\n                      </n-button>\n                      <n-button secondary @click=\"exportChatEvalCasesJson\">\n                        导出测试用例集 (.json)\n                      </n-button>\n                      <n-tag v-if=\"chatTelemetry?.modelName\" type=\"info\" size=\"small\" round>\n                        活跃模型: {{ chatTelemetry.modelName }}\n                      </n-tag>\n                    </n-space>\n                  </n-space>\n                </n-card>\n\n                <!-- 实时评测进度看板 -->\n                <n-card v-if=\"chatEvalRunning\" size=\"small\">\n                  <n-space vertical :size=\"6\">\n                    <n-space justify=\"space-between\" align=\"center\">\n                      <n-text depth=\"2\">\n                        <span>⚡ 正在评测 {{ chatEvalProgress.currentCase }} ...</span>\n                      </n-text>\n                      <n-tag type=\"info\" size=\"small\" round>\n                        {{ chatEvalProgress.current }} / {{ chatEvalProgress.total }} ({{ chatEvalProgress.percent }}%)\n                      </n-tag>\n                    </n-space>\n                    <n-progress type=\"line\" :percentage=\"chatEvalProgress.percent\" :show-indicator=\"false\" processing status=\"info\" />\n                  </n-space>\n                </n-card>\n\n                <!-- 综合评估诊断与画像看板 -->\n                <n-card v-if=\"chatEvalStats.testedCount > 0\" title=\"全维评测结果与质量诊断\">\n                  <template #header-extra>\n                    <n-tag :type=\"chatEvalStats.gradeType\" size=\"small\" round>\n                      {{ chatEvalStats.grade }} 级综合质量评估\n                    </n-tag>\n                  </template>\n\n                  <!-- 诊断引导面板 -->\n                  <n-alert :type=\"chatEvalStats.gradeType\" style=\"margin-bottom: 16px\" :title=\"chatEvalStats.title\">\n                    <div style=\"margin-top: 6px; line-height: 1.8\">\n                      <div>已完成 <strong>{{ chatEvalStats.testedCount }}</strong> / {{ chatEvalStats.total }} 项用例，通过 <strong>{{ chatEvalStats.passedCount }}</strong> 项，未通过 <strong>{{ chatEvalStats.failedCount }}</strong> 项，总体通过率 <strong>{{ chatEvalStats.passRate }}%</strong>，平均单例耗时 <strong>{{ chatEvalStats.avgLatency }}ms</strong>，平均首字 <strong>{{ chatEvalStats.avgTtft }}ms</strong>，平均吞吐 <strong>{{ chatEvalStats.avgTps }} tok/s</strong>。</div>\n                      <div v-if=\"chatEvalStats.failedCount > 0\" style=\"margin-top: 4px; color: #d03050\">\n                        提示：存在未通过项，可通过上方「导出评测报告」查看失效模式归因分析，或下载「用例集」用于模型微调与量化校准。\n                      </div>\n                    </div>\n                  </n-alert>\n\n                  <!-- 核心统计网格 -->\n                  <n-grid :cols=\"7\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n                    <n-gi>\n                      <n-statistic label=\"已测用例\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold\">{{ chatEvalStats.testedCount }}</span>\n                          <span style=\"font-size: 12px; color: #888; margin-left: 4px\">/ {{ chatEvalStats.total }}</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"通过用例\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold; color: var(--n-success-color, #18a058)\">{{ chatEvalStats.passedCount }}</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"未通过用例\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold; color: var(--n-error-color, #d03050)\">{{ chatEvalStats.failedCount }}</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"总体通过率\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold; color: var(--n-value-text-color)\">{{ chatEvalStats.passRate }}</span>\n                          <span style=\"font-size: 12px; color: #888; margin-left: 4px\">%</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"平均单例延迟\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold\">{{ chatEvalStats.avgLatency }}</span>\n                          <span style=\"font-size: 12px; color: #888; margin-left: 4px\">ms</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"平均首字 (TTFT)\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold\">{{ chatEvalStats.avgTtft }}</span>\n                          <span style=\"font-size: 12px; color: #888; margin-left: 4px\">ms</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                    <n-gi>\n                      <n-statistic label=\"平均生成吞吐\">\n                        <template #default>\n                          <span style=\"font-size: 20px; font-weight: bold\">{{ chatEvalStats.avgTps }}</span>\n                          <span style=\"font-size: 12px; color: #888; margin-left: 4px\">tok/s</span>\n                        </template>\n                      </n-statistic>\n                    </n-gi>\n                  </n-grid>\n\n                  <!-- 维度画像折叠卡片 -->\n                  <n-collapse style=\"margin-bottom: 12px\">\n                    <n-collapse-item title=\"📊 7 大核心能力维度画像表\" name=\"dimensions-panel\">\n                      <n-data-table\n                        :columns=\"chatEvalDimensionColumns\"\n                        :data=\"chatEvalStats.dimensions\"\n                        :bordered=\"false\"\n                        size=\"small\"\n                      />\n                    </n-collapse-item>\n                  </n-collapse>\n                </n-card>\n\n                <!-- 用例明细表卡片 -->\n                <n-card title=\"评测用例矩阵与实测回显\">\n                  <template #header-extra>\n                    <n-tag size=\"small\" type=\"default\" round>\n                      共 12 项场景用例\n                    </n-tag>\n                  </template>\n                  <n-data-table\n                    :columns=\"chatEvalColumns\"\n                    :data=\"chatEvalCases\"\n                    :bordered=\"false\"\n                    size=\"small\"\n                    :scroll-x=\"1200\"\n                  />\n                </n-card>\n              </n-space>\n            </n-tab-pane>\n\n            <n-tab-pane name=\"benchmark\" tab=\"实时流式测速与吞吐\">\n              <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n<!-- 聊天模型实时流式测速靶场 (TTFT & TPS) -->\n          <n-card title=\"聊天生成性能靶场 (TTFT & TPS 压测)\">\n            <n-space vertical :size=\"12\">\n              <n-text depth=\"3\">\n                基于真实流式 SSE (/v1/chat/completions) 进行高精度时间戳采样，评测首字延迟 (Time to First Token, TTFT)、生成吞吐率 (Tokens Per Second, TPS) 及生成效率。\n              </n-text>\n\n              <!-- 预设 Prompt 按钮组 -->\n              <n-space align=\"center\">\n                <n-text depth=\"2\" style=\"font-size: 13px\">快速选择预设用例：</n-text>\n                <n-button\n                  v-for=\"preset in chatPresetPrompts\"\n                  :key=\"preset.label\"\n                  size=\"small\"\n                  secondary\n                  @click=\"selectChatPreset(preset)\"\n                >\n                  {{ preset.label }}\n                </n-button>\n              </n-space>\n\n              <n-grid :cols=\"4\" :x-gap=\"16\">\n                <n-gi :span=\"2\">\n                  <n-form-item label=\"测试 Prompt\" :show-feedback=\"false\">\n                    <n-input\n                      v-model:value=\"chatBenchmarkPrompt\"\n                      type=\"textarea\"\n                      :rows=\"3\"\n                      placeholder=\"请输入用于测试聊天模型的提示词...\"\n                    />\n                  </n-form-item>\n                </n-gi>\n                <n-gi>\n                  <n-form-item label=\"Max Tokens (最大生成长度)\" :show-feedback=\"false\">\n                    <n-input-number v-model:value=\"chatBenchmarkMaxTokens\" :min=\"16\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n                  </n-form-item>\n                </n-gi>\n                <n-gi>\n                  <n-form-item label=\"Temperature (采样温度)\" :show-feedback=\"false\">\n                    <n-input-number v-model:value=\"chatBenchmarkTemperature\" :min=\"0\" :max=\"2\" :step=\"0.1\" style=\"width: 100%\" />\n                  </n-form-item>\n                </n-gi>\n              </n-grid>\n\n              <n-space align=\"center\">\n                <n-button type=\"primary\" :loading=\"chatBenchmarkLoading\" @click=\"runChatBenchmark\">\n                  运行流式测速\n                </n-button>\n                <n-checkbox v-model:checked=\"chatBenchmarkStream\">\n                  实时流式接收 (Stream SSE)\n                </n-checkbox>\n                <n-tag v-if=\"chatBenchmarkStreaming\" type=\"warning\" size=\"small\" round>\n                  ⚡ 正在流式生成中...\n                </n-tag>\n              </n-space>\n            </n-space>\n          </n-card>\n\n          <!-- 测速结果与指标面板 -->\n          <n-card v-if=\"chatBenchmarkLoading || chatBenchmarkOutput || chatBenchmarkMetrics\" title=\"生成性能诊断与指标总览\" style=\"margin-top: 4px\">\n            <template #header-extra>\n              <n-tag v-if=\"chatBenchmarkEvaluation\" :type=\"chatBenchmarkEvaluation.gradeType\" size=\"small\" round>\n                {{ chatBenchmarkEvaluation.grade }} 级生成性能\n              </n-tag>\n              <n-tag v-else-if=\"chatBenchmarkLoading\" type=\"warning\" size=\"small\" round>\n                测速采样中...\n              </n-tag>\n            </template>\n\n            <!-- 测速失败错误提示 -->\n            <n-alert v-if=\"chatBenchmarkOutput && chatBenchmarkOutput.startsWith('[测速失败]')\" type=\"error\" style=\"margin-bottom: 16px\" title=\"测速异常\">\n              {{ chatBenchmarkOutput }}\n            </n-alert>\n\n            <!-- 诊断与落地引导面板 -->\n            <n-alert v-if=\"chatBenchmarkEvaluation\" :type=\"chatBenchmarkEvaluation.gradeType\" style=\"margin-bottom: 16px\" :title=\"chatBenchmarkEvaluation.title\">\n              <div style=\"margin-top: 6px; line-height: 1.8\">\n                <ul style=\"margin: 0; padding-left: 18px\">\n                  <li v-for=\"(tip, i) in chatBenchmarkEvaluation.tips\" :key=\"i\">{{ tip }}</li>\n                </ul>\n              </div>\n            </n-alert>\n\n            <!-- 核心指标网格 -->\n            <n-grid v-if=\"chatBenchmarkMetrics\" :cols=\"6\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n              <n-gi>\n                <n-statistic label=\"首字延迟 (TTFT)\">\n                  <template #default>\n                    <span style=\"font-size: 20px; font-weight: bold; color: var(--n-value-text-color)\">{{ chatBenchmarkMetrics.ttftMs }}</span>\n                    <span style=\"font-size: 12px; margin-left: 4px; color: #888\">ms</span>\n                  </template>\n                </n-statistic>\n              </n-gi>\n              <n-gi>\n                <n-statistic label=\"生成吞吐量 (TPS)\">\n                  <template #default>\n                    <span style=\"font-size: 20px; font-weight: bold; color: var(--n-value-text-color)\">{{ chatBenchmarkMetrics.tps }}</span>\n                    <span style=\"font-size: 12px; margin-left: 4px; color: #888\">tok/s</span>\n                  </template>\n                </n-statistic>\n              </n-gi>\n              <n-gi>\n                <n-statistic label=\"总耗时\">\n                  <template #default>\n                    <span style=\"font-size: 20px; font-weight: bold; color: var(--n-value-text-color)\">{{ chatBenchmarkMetrics.totalMs }}</span>\n                    <span style=\"font-size: 12px; margin-left: 4px; color: #888\">ms</span>\n                  </template>\n                </n-statistic>\n              </n-gi>\n              <n-gi>\n                <n-statistic label=\"生成耗时\">\n                  <template #default>\n                    <span style=\"font-size: 20px; font-weight: bold; color: var(--n-value-text-color)\">{{ chatBenchmarkMetrics.generationMs }}</span>\n                    <span style=\"font-size: 12px; margin-left: 4px; color: #888\">ms</span>\n                  </template>\n                </n-statistic>\n              </n-gi>\n              <n-gi>\n                <n-statistic label=\"预估 Token 数\" :value=\"chatBenchmarkMetrics.tokensGenerated\" />\n              </n-gi>\n              <n-gi>\n                <n-statistic label=\"调用模型\" :value=\"chatBenchmarkMetrics.modelUsed\" />\n              </n-gi>\n            </n-grid>\n\n            <!-- 实时打字机生成回显 -->\n            <n-divider>模型实际输出回显</n-divider>\n            <div style=\"background: rgba(128, 128, 128, 0.05); border: 1px solid rgba(128, 128, 128, 0.15); border-radius: 6px; padding: 12px 16px; font-family: monospace; white-space: pre-wrap; line-height: 1.6; max-height: 320px; overflow-y: auto;\">\n              {{ chatBenchmarkOutput || (chatBenchmarkLoading ? '⚡ 正在建立流式连接并生成中...' : '（等待生成中...）') }}\n            </div>\n          </n-card>\n              </n-space>\n            </n-tab-pane>\n          </n-tabs>\n        </n-space>\n      </n-tab-pane>\n    </n-tabs>\n  </n-space>";
export default component;

