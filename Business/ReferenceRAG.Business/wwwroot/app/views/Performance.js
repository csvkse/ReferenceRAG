import { defineComponent as _defineComponent } from 'vue';
import { ref, h, computed, onMounted } from 'vue';
import { NTag, NCode, NDivider } from 'naive-ui';
import { performanceApi, semanticTestApi, rerankTestApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Performance',
    setup(__props, { expose: __expose }) {
        __expose();
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
        onMounted(() => { runProbe(); });
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
            '反义词': (a) => a <= 0.5,
            '情感反义': (a) => a <= 0.5,
            '状态反义': (a) => a <= 0.5,
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
        // 运行全部测试（所有 100 个用例）
        const runSemanticTest = async () => {
            semanticLoading.value = true;
            semanticResult.value = null;
            activeTestCategory.value = null;
            try {
                if (useRemoteForSemantic.value && remoteApi.value.baseUrl) {
                    // 使用远程 OpenAI 兼容 API
                    const results = await runSemanticTestWithRemote(semanticCategories);
                    semanticResult.value = results;
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
                    for (const category of semanticCategories) {
                        if (category.cases.length === 0)
                            continue;
                        // 每个 case 单独测试：query vs text
                        for (const c of category.cases) {
                            const res = await semanticTestApi.shortText({
                                query: c.query,
                                candidates: [{ text: c.text, expectedSimilarity: c.expectedSimilarity }]
                            });
                            if (res.data.candidates && res.data.candidates[0]) {
                                const candidate = res.data.candidates[0];
                                candidate._category = c.category;
                                allCandidates.push(candidate);
                                // 统计计算
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
                    }
                    // 计算最终指标
                    const n = allCandidates.length;
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
                        rankingAccuracy: 0, // 排序准确度需要重新设计
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
            { id: '1', text: 'Python是一门简单易学的编程语言，适合初学者入门', isRelevant: true },
            { id: '2', text: 'Java是一种面向对象的编程语言，广泛应用于企业开发', isRelevant: false },
            { id: '3', text: 'Python数据分析教程：从入门到精通', isRelevant: true },
            { id: '4', text: '机器学习算法原理与实现', isRelevant: false },
            { id: '5', text: 'JavaScript前端开发实战指南', isRelevant: false }
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
                title: '相关性', key: 'isRelevant', width: 80,
                render: (row) => h(NTag, { type: row.isRelevant ? 'success' : 'default', size: 'small' }, {
                    default: () => row.isRelevant ? '相关' : '不相关'
                })
            },
            { title: '相似度', key: 'similarity', width: 100, render: (row) => (row.similarity || 0).toFixed(4) },
            { title: '文本', key: 'text', ellipsis: { tooltip: true } },
            { title: 'Tokens', key: 'tokenCount', width: 80 }
        ];
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
        const parseRerankDocuments = () => {
            try {
                rerankDocuments.value = JSON.parse(rerankDocumentsJson.value);
            }
            catch {
                // 解析失败，使用默认值
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
        // 运行单个预设测试
        const runRerankPreset = async (presetName) => {
            rerankLoading.value = true;
            activeRerankPreset.value = presetName;
            rerankResult.value = null;
            try {
                const res = await rerankTestApi.runPreset(presetName);
                rerankResult.value = res.data;
            }
            catch (e) {
                console.error(e);
            }
            finally {
                rerankLoading.value = false;
                activeRerankPreset.value = null;
            }
        };
        // 运行所有预设测试
        const runAllRerankPresets = async () => {
            if (rerankPresets.value.length === 0) {
                await loadRerankPresets();
            }
            rerankLoading.value = true;
            rerankResult.value = null;
            try {
                // 依次运行所有预设
                for (const preset of rerankPresets.value) {
                    activeRerankPreset.value = preset.name;
                    await rerankTestApi.runPreset(preset.name);
                }
                // 最后运行一个作为展示
                const lastPreset = rerankPresets.value[rerankPresets.value.length - 1];
                const res = await rerankTestApi.runPreset(lastPreset.name);
                rerankResult.value = res.data;
                // 刷新统计
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
            parseRerankDocuments();
            if (!rerankQuery.value || rerankDocuments.value.length === 0)
                return;
            rerankLoading.value = true;
            rerankResult.value = null;
            try {
                if (useRemoteForRerank.value && remoteApi.value.baseUrl) {
                    const result = await runRerankWithRemoteApi();
                    rerankResult.value = result;
                }
                else {
                    const res = await rerankTestApi.test({
                        query: rerankQuery.value,
                        documents: rerankDocuments.value,
                        modelName: rerankModelName.value || undefined
                    });
                    rerankResult.value = res.data;
                }
            }
            catch (e) {
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
                text: d.text,
                expectedRelevance: d.expectedRelevance,
                relevanceScore: cosineSimilarity(queryEmbedding, docEmbeddings[i])
            })).sort((a, b) => b.relevanceScore - a.relevanceScore);
            // 添加排名
            const rankedDocs = results.map((r, i) => ({
                id: r.id,
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
                    const expectedRank = n - docsWithExpected.findIndex(doc => doc.id === d.id);
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
        const rerankDocColumns = [
            { title: '排名', key: 'rank', width: 60 },
            {
                title: '相关性', key: 'relevanceScore', width: 100,
                render: (row) => {
                    const score = row.relevanceScore || 0;
                    return h(NTag, {
                        type: score >= 0.7 ? 'success' : score >= 0.4 ? 'warning' : 'default',
                        size: 'small'
                    }, { default: () => score.toFixed(3) });
                }
            },
            { title: '期望', key: 'expectedRelevance', width: 80, render: (row) => row.expectedRelevance !== undefined ? (row.expectedRelevance).toFixed(2) : '-' },
            { title: '偏差', key: 'deviation', width: 80, render: (row) => row.deviation !== undefined ? row.deviation.toFixed(3) : '-' },
            { title: '文档', key: 'text', ellipsis: { tooltip: true } },
            { title: 'ID', key: 'id', width: 80 }
        ];
        const __returned__ = { probe, probeLoading, runProbe, semanticLoading, customSemanticLoading, showCustomSemantic, semanticResult, activeTestCategory, useRemoteForSemantic, customSemantic, customSemanticResult, categoryPassCriteria, defaultPassCriteria, evaluatePassByCategory, semanticCategories, runSemanticTest, runSemanticTestWithRemote, runCategoryTest, runCustomSemantic, getSimilarityType, categoryStats, overallPassRate, categoryColumns, semanticColumns, retrievalQuery, retrievalTopK, retrievalLoading, showRetrievalEditor, retrievalPassagesJson, retrievalResult, useRemoteForRetrieval, sampleRetrievalPassages, loadSampleRetrieval, runRetrievalTest, runRetrievalWithRemote, retrievalColumns, benchmark, benchmarkLoading, benchmarkResult, useRemoteForBenchmark, benchmarkUsedRemote, runBenchmark, runBenchmarkWithRemoteApi, generateSampleText, splitIntoChunks, estimateTokens, batchTextLength, batchLoading, batchResult, batchColumns, runBatchTest, memVectorCount, memDimension, memLoading, memResult, runMemoryTest, showApiConfig, apiConnected, apiTesting, apiTestResult, remoteApi, savedApiConfig, remoteLoading, remoteActiveCategory, remoteResults, saveApiConfig, testApiConnection, cosineSimilarity, callEmbeddingApi, processCategoryResults, runRemoteTest, runRemoteCategoryTest, runRemoteAllCategories, remoteTotalTime, remoteMae, remoteRmse, remoteCorrelation, remoteOverallPassRate, remoteCategoryStats, remoteCategoryColumns, remoteDetailColumns, apiTest, apiFuncTesting, apiFuncResult, apiHistory, apiBatchColumns, apiHistoryColumns, loadPresetConfig, callTestEmbeddingApi, runApiFunctionTest, runApiBatchTest, rerankQuery, rerankModelName, rerankLoading, showCustomRerank, rerankDocumentsJson, rerankDocuments, rerankResult, useRemoteForRerank, rerankPresets, activeRerankPreset, rerankStatistics, rerankBenchmarkLoading, benchmarkConcurrency, benchmarkTotalRequests, benchmarkDocCount, rerankBenchmarkResult, sampleRerankDocuments, loadSampleRerankDocuments, parseRerankDocuments, loadRerankPresets, runRerankPreset, runAllRerankPresets, runCustomRerankTest, loadRerankStatistics, runRerankBenchmark, runRerankWithRemoteApi, rerankDocColumns, get NTag() { return NTag; }, get NCode() { return NCode; }, get NDivider() { return NDivider; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- Global OpenAI API Config -->\n    <n-card title=\"OpenAI 兼容 API 配置\" size=\"small\" v-if=\"showApiConfig\">\n      <template #header-extra>\n        <n-button text size=\"small\" @click=\"showApiConfig = false\">\n          <template #icon><n-icon><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"currentColor\"><path d=\"M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z\"/></svg></n-icon></template>\n        </n-button>\n      </template>\n      <n-grid :cols=\"5\" :x-gap=\"16\">\n        <n-gi>\n          <n-form-item label=\"Base URL\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.baseUrl\" placeholder=\"http://localhost:11434\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"API Key\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.apiKey\" type=\"password\" placeholder=\"留空则不发送\" show-password-on=\"click\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"模型名称\" :show-feedback=\"false\">\n            <n-input v-model:value=\"remoteApi.model\" placeholder=\"bge-m3\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"批量大小\" :show-feedback=\"false\">\n            <n-input-number v-model:value=\"remoteApi.batchSize\" :min=\"1\" :max=\"128\" style=\"width: 100%\" @blur=\"saveApiConfig\" />\n          </n-form-item>\n        </n-gi>\n        <n-gi>\n          <n-form-item label=\"状态\" :show-feedback=\"false\">\n            <n-space>\n              <n-tag :type=\"apiConnected ? 'success' : 'default'\" size=\"small\">\n                {{ apiConnected ? '已连接' : '未测试' }}\n              </n-tag>\n              <n-button size=\"small\" :loading=\"apiTesting\" @click=\"testApiConnection\">\n                测试连接\n              </n-button>\n            </n-space>\n          </n-form-item>\n        </n-gi>\n      </n-grid>\n      <n-text depth=\"3\" style=\"margin-top: 8px; display: block;\">\n        支持 Ollama、vLLM、Xinference、LM Studio、OpenAI 等任意 OpenAI 兼容接口。配置自动保存到 localStorage。\n      </n-text>\n    </n-card>\n\n    <n-space v-if=\"!showApiConfig && remoteApi.baseUrl\" align=\"center\" style=\"margin-bottom: 0\">\n      <n-tag type=\"info\" size=\"small\">\n        <template #icon><n-icon><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"currentColor\"><path d=\"M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z\"/></svg></n-icon></template>\n        {{ remoteApi.model || '远程API' }} - {{ remoteApi.baseUrl }}\n      </n-tag>\n      <n-button text size=\"small\" @click=\"showApiConfig = true\">修改配置</n-button>\n    </n-space>\n\n    <n-button v-if=\"!showApiConfig && !remoteApi.baseUrl\" size=\"small\" @click=\"showApiConfig = true\">\n      配置 OpenAI 兼容 API\n    </n-button>\n\n    <n-tabs type=\"line\" animated>\n      <!-- Semantic Similarity Test -->\n      <n-tab-pane name=\"semantic\" tab=\"语义相似度测试\">\n\n        <!-- 模型诊断卡片 -->\n        <n-card size=\"small\" style=\"margin-bottom: 12px\">\n          <template #header>\n            <n-space align=\"center\" :size=\"8\">\n              <span>模型诊断</span>\n              <n-tag v-if=\"probe\" :type=\"probe.healthy ? 'success' : (probe.isSimulationMode ? 'error' : 'warning')\" size=\"small\">\n                {{ probe.isSimulationMode ? '⚠ 模拟模式' : (probe.healthy ? '✓ 正常' : '⚠ 异常') }}\n              </n-tag>\n              <n-tag v-else-if=\"probeLoading\" type=\"default\" size=\"small\">检测中…</n-tag>\n            </n-space>\n          </template>\n          <template #header-extra>\n            <n-button text size=\"small\" :loading=\"probeLoading\" @click=\"runProbe\">刷新</n-button>\n          </template>\n\n          <n-spin :show=\"probeLoading\" style=\"min-height: 48px\">\n            <div v-if=\"probe\">\n              <!-- 模拟模式警告 -->\n              <n-alert v-if=\"probe.isSimulationMode\" type=\"error\" style=\"margin-bottom: 10px\">\n                <template #header>模型未加载 — 正在使用随机向量</template>\n                当前嵌入服务运行在<strong>模拟模式</strong>，所有相似度均为随机值，测试结果无意义。\n                请在「模型管理」页切换到已下载的模型。\n              </n-alert>\n              <n-alert v-else-if=\"!probe.healthy && !probe.error\" type=\"warning\" style=\"margin-bottom: 10px\">\n                <template #header>向量质量异常</template>\n                自相似度 {{ probe.selfSimilarity.toFixed(3) }} / 高相似对 {{ probe.highSimilarityActual.toFixed(3) }}（期望 &gt;0.4）/ 低相似对 {{ probe.lowSimilarityActual.toFixed(3) }}（期望 &lt;0.35）。\n                可能原因：ONNX 格式不兼容、模型未完整转换、或该模型的相似度量程天然偏低。\n              </n-alert>\n              <n-alert v-if=\"probe.error\" type=\"error\" style=\"margin-bottom: 10px\">\n                探针失败：{{ probe.error }}\n              </n-alert>\n\n              <n-grid :cols=\"6\" :x-gap=\"12\">\n                <n-gi>\n                  <n-statistic label=\"模型\">\n                    <template #default>\n                      <n-text style=\"font-size: 12px; word-break: break-all\">{{ probe.modelName || '—' }}</n-text>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"维度\" :value=\"probe.dimension\" />\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"自相似度\">\n                    <template #default>\n                      <n-tag :type=\"probe.selfSimilarity > 0.99 ? 'success' : 'error'\" size=\"small\">\n                        {{ probe.selfSimilarity.toFixed(4) }}\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"同义词对\">\n                    <template #default>\n                      <n-tag :type=\"probe.highSimilarityActual >= 0.4 ? 'success' : 'warning'\" size=\"small\">\n                        {{ probe.highSimilarityActual.toFixed(3) }}\n                        <n-text depth=\"3\" style=\"font-size: 10px\"> / {{ probe.highSimilarityExpected.toFixed(2) }}</n-text>\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"无关词对\">\n                    <template #default>\n                      <n-tag :type=\"probe.lowSimilarityActual <= 0.35 ? 'success' : 'warning'\" size=\"small\">\n                        {{ probe.lowSimilarityActual.toFixed(3) }}\n                        <n-text depth=\"3\" style=\"font-size: 10px\"> / {{ probe.lowSimilarityExpected.toFixed(2) }}</n-text>\n                      </n-tag>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n                <n-gi>\n                  <n-statistic label=\"向量采样（前8维）\">\n                    <template #default>\n                      <n-text style=\"font-size: 10px; font-family: monospace; word-break: break-all\">\n                        [{{ probe.vectorSample.map(v => v.toFixed(3)).join(', ') }}]\n                      </n-text>\n                    </template>\n                  </n-statistic>\n                </n-gi>\n              </n-grid>\n            </div>\n            <n-text v-else-if=\"!probeLoading\" depth=\"3\">点击「刷新」运行诊断</n-text>\n          </n-spin>\n        </n-card>\n\n        <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试模型的语义理解能力：同义词、反义词、多义词、跨语言、小数、特殊符号、非中英文等。中文测试 64 个用例（14 分类），英文测试 36 个用例（8 分类）。</n-text>\n            <n-space align=\"center\">\n              <n-button type=\"primary\" :loading=\"semanticLoading\" @click=\"runSemanticTest\">\n                运行全部测试（100 项）\n              </n-button>\n              <n-button @click=\"showCustomSemantic = true\">\n                自定义测试\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForSemantic\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForSemantic && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <!-- Category Buttons -->\n        <n-card title=\"分类测试\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"cat in semanticCategories\"\n              :key=\"cat.label\"\n              :loading=\"semanticLoading && activeTestCategory === cat.label\"\n              size=\"small\"\n              :type=\"activeTestCategory === cat.label ? 'primary' : 'default'\"\n              @click=\"runCategoryTest(cat)\"\n            >\n              {{ cat.label }}（{{ cat.cases.length }}）\n            </n-button>\n          </n-space>\n          <n-text v-if=\"activeTestCategory\" depth=\"3\" style=\"margin-top: 8px; display: block\">\n            {{ semanticCategories.find(c => c.label === activeTestCategory)?.description }}\n          </n-text>\n        </n-card>\n\n        <!-- Custom Semantic Test Dialog -->\n        <n-modal v-model:show=\"showCustomSemantic\" preset=\"dialog\" title=\"自定义语义测试\">\n          <n-space vertical>\n            <n-form-item label=\"文本 1\">\n              <n-input v-model:value=\"customSemantic.text1\" placeholder=\"输入第一段文本\" />\n            </n-form-item>\n            <n-form-item label=\"文本 2\">\n              <n-input v-model:value=\"customSemantic.text2\" placeholder=\"输入第二段文本\" />\n            </n-form-item>\n            <n-form-item label=\"期望相似度\">\n              <n-slider v-model:value=\"customSemantic.expected\" :min=\"0\" :max=\"100\" :step=\"5\" />\n            </n-form-item>\n            <n-button type=\"primary\" :loading=\"customSemanticLoading\" @click=\"runCustomSemantic\">\n              测试\n            </n-button>\n          </n-space>\n          <n-card v-if=\"customSemanticResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n            <n-descriptions :column=\"2\">\n              <n-descriptions-item label=\"实际相似度\">\n                <n-tag :type=\"getSimilarityType(customSemanticResult.candidates?.[0]?.similarity || 0, customSemantic.expected / 100)\">\n                  {{ ((customSemanticResult.candidates?.[0]?.similarity || 0) * 100).toFixed(1) }}%\n                </n-tag>\n              </n-descriptions-item>\n              <n-descriptions-item label=\"期望相似度\">{{ customSemantic.expected }}%</n-descriptions-item>\n              <n-descriptions-item label=\"偏差\">\n                {{ (customSemanticResult.candidates?.[0]?.deviation ?? 0).toFixed(3) }}\n              </n-descriptions-item>\n              <n-descriptions-item label=\"Token 计数\">\n                {{ customSemanticResult.queryTokenCount || '-' }}\n              </n-descriptions-item>\n            </n-descriptions>\n          </n-card>\n        </n-modal>\n\n        <!-- Results -->\n        <n-card v-if=\"semanticResult\" title=\"测试结果\" style=\"margin-top: 16px\">\n          <!-- Summary -->\n          <n-space style=\"margin-bottom: 16px\">\n            <n-tag type=\"success\" size=\"large\">\n              通过: {{ overallPassRate.passed }} / {{ overallPassRate.total }}（{{ overallPassRate.rate }}）\n            </n-tag>\n            <n-tag v-if=\"semanticResult.meanAbsoluteError != null\" size=\"large\">\n              MAE: {{ semanticResult.meanAbsoluteError.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"semanticResult.rootMeanSquareError != null\" size=\"large\">\n              RMSE: {{ semanticResult.rootMeanSquareError.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"semanticResult.correlationCoefficient != null\" size=\"large\">\n              相关性: {{ semanticResult.correlationCoefficient.toFixed(3) }}\n            </n-tag>\n          </n-space>\n\n          <!-- Category Stats -->\n          <n-data-table\n            v-if=\"categoryStats.length > 0\"\n            :columns=\"categoryColumns\"\n            :data=\"categoryStats\"\n            :bordered=\"false\"\n            size=\"small\"\n            style=\"margin-bottom: 16px\"\n          />\n\n          <!-- Detail Table -->\n          <n-data-table\n            :columns=\"semanticColumns\"\n            :data=\"semanticResult.candidates || []\"\n            :bordered=\"false\"\n            size=\"small\"\n            :max-height=\"400\"\n            :scroll-x=\"800\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Retrieval Quality Test (long text) -->\n      <n-tab-pane name=\"retrieval\" tab=\"检索质量测试\">\n        <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试模型的文档检索能力：给定 query 和标注相关性的 passages，评估排序准确性。</n-text>\n            <n-grid :cols=\"3\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"查询文本\">\n                  <n-input v-model:value=\"retrievalQuery\" placeholder=\"输入查询文本\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"Top K\">\n                  <n-input-number v-model:value=\"retrievalTopK\" :min=\"1\" :max=\"20\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"Passages (JSON)\">\n                  <n-button size=\"small\" @click=\"showRetrievalEditor = true\">编辑 Passages</n-button>\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"retrievalLoading\" @click=\"runRetrievalTest\">\n                运行检索测试\n              </n-button>\n              <n-button @click=\"loadSampleRetrieval\">\n                加载示例数据\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForRetrieval\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForRetrieval && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <n-modal v-model:show=\"showRetrievalEditor\" preset=\"dialog\" title=\"编辑 Passages (JSON)\" style=\"width: 60vw\">\n          <n-input\n            v-model:value=\"retrievalPassagesJson\"\n            type=\"textarea\"\n            :rows=\"12\"\n            placeholder='[{\"id\":\"1\",\"text\":\"文档内容\",\"isRelevant\":true}]'\n            style=\"font-family: monospace\"\n          />\n        </n-modal>\n\n        <n-card v-if=\"retrievalResult\" title=\"检索结果\" style=\"margin-top: 16px\">\n          <!-- Metrics -->\n          <n-grid :cols=\"5\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n            <n-gi>\n              <n-statistic label=\"Precision\" :value=\"(retrievalResult.precision || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Recall\" :value=\"(retrievalResult.recall || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"F1 Score\" :value=\"(retrievalResult.f1Score || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"NDCG\" :value=\"(retrievalResult.ndcg || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MRR\" :value=\"(retrievalResult.mrr || 0).toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"3\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"Query Embedding\" :value=\"retrievalResult.queryEmbeddingMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Total Embedding\" :value=\"retrievalResult.totalEmbeddingMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"Avg Embedding\" :value=\"(retrievalResult.avgEmbeddingMs || 0).toFixed(1)\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n          </n-grid>\n\n          <!-- Passage Ranking -->\n          <n-divider>Passage 排序</n-divider>\n          <n-data-table\n            :columns=\"retrievalColumns\"\n            :data=\"retrievalResult.passages || []\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Benchmark -->\n      <n-tab-pane name=\"benchmark\" tab=\"性能基准\">\n        <n-card>\n          <n-space vertical>\n            <n-grid :cols=\"useRemoteForBenchmark ? 6 : 5\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"文本长度\">\n                  <n-input-number v-model:value=\"benchmark.textLength\" :min=\"1000\" :max=\"1000000\" :step=\"10000\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"分段大小\">\n                  <n-input-number v-model:value=\"benchmark.chunkSize\" :min=\"64\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"重叠大小\">\n                  <n-input-number v-model:value=\"benchmark.overlapSize\" :min=\"0\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"批量大小\">\n                  <n-input-number v-model:value=\"benchmark.batchSize\" :min=\"1\" :max=\"128\" :step=\"1\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi v-if=\"useRemoteForBenchmark\">\n                <n-form-item label=\"并发数\">\n                  <n-input-number v-model:value=\"benchmark.concurrency\" :min=\"1\" :max=\"32\" :step=\"1\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"包含代码块\">\n                  <n-switch v-model:value=\"benchmark.includeCodeBlocks\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"benchmarkLoading\" @click=\"runBenchmark\">\n                运行基准测试\n              </n-button>\n              <n-checkbox v-model:checked=\"useRemoteForBenchmark\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForBenchmark && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"benchmarkResult\" title=\"基准测试结果\" style=\"margin-top: 16px\">\n          <n-space style=\"margin-bottom: 12px\">\n            <n-tag v-if=\"benchmarkUsedRemote\" type=\"info\" size=\"small\">\n              {{ remoteApi.model || '远程API' }}\n            </n-tag>\n            <n-tag v-else type=\"success\" size=\"small\">本地模型</n-tag>\n          </n-space>\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"分段耗时\" :value=\"benchmarkResult.chunkingMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"Token 计数\" :value=\"benchmarkResult.tokenCountMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"向量化耗时\" :value=\"benchmarkResult.embeddingMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n            <n-gi><n-statistic label=\"总耗时\" :value=\"benchmarkResult.totalMs\" /><n-text depth=\"3\">ms</n-text></n-gi>\n          </n-grid>\n          <n-divider />\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"分段数\" :value=\"benchmarkResult.chunkCount\" /></n-gi>\n            <n-gi><n-statistic label=\"向量数\" :value=\"benchmarkResult.vectorCount\" /></n-gi>\n            <n-gi><n-statistic label=\"向量维度\" :value=\"benchmarkResult.vectorDimension\" /></n-gi>\n            <n-gi><n-statistic label=\"预估内存\" :value=\"benchmarkResult.estimatedMemoryMB.toFixed(1)\" /><n-text depth=\"3\">MB</n-text></n-gi>\n          </n-grid>\n          <n-divider />\n          <n-grid :cols=\"4\" :x-gap=\"20\">\n            <n-gi><n-statistic label=\"Tokens/s\" :value=\"benchmarkResult.tokensPerSecond.toFixed(0)\" /></n-gi>\n            <n-gi><n-statistic label=\"Chars/s\" :value=\"benchmarkResult.charsPerSecond.toFixed(0)\" /></n-gi>\n            <n-gi><n-statistic label=\"Chunks/s\" :value=\"benchmarkResult.chunksPerSecond.toFixed(2)\" /></n-gi>\n            <n-gi><n-statistic label=\"Vectors/s\" :value=\"benchmarkResult.vectorsPerSecond.toFixed(2)\" /></n-gi>\n          </n-grid>\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Batch & Memory -->\n      <n-tab-pane name=\"optimization\" tab=\"批量与内存\">\n        <n-grid :cols=\"2\" :x-gap=\"20\">\n          <n-gi>\n            <n-card title=\"批量大小优化\">\n              <n-space vertical>\n                <n-form-item label=\"文本长度\">\n                  <n-input-number v-model:value=\"batchTextLength\" :min=\"5000\" :max=\"1000000\" :step=\"10000\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-button type=\"primary\" :loading=\"batchLoading\" @click=\"runBatchTest\">运行测试</n-button>\n              </n-space>\n            </n-card>\n            <n-card v-if=\"batchResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n              <n-data-table :columns=\"batchColumns\" :data=\"batchResult.results\" :bordered=\"false\" size=\"small\" />\n            </n-card>\n          </n-gi>\n          <n-gi>\n            <n-card title=\"内存测试\">\n              <n-space vertical>\n                <n-grid :cols=\"2\" :x-gap=\"12\">\n                  <n-gi>\n                    <n-form-item label=\"向量数量\">\n                      <n-input-number v-model:value=\"memVectorCount\" :min=\"1\" :max=\"100000\" style=\"width: 100%\" />\n                    </n-form-item>\n                  </n-gi>\n                  <n-gi>\n                    <n-form-item label=\"向量维度\">\n                      <n-input-number v-model:value=\"memDimension\" :min=\"1\" :max=\"4096\" style=\"width: 100%\" />\n                    </n-form-item>\n                  </n-gi>\n                </n-grid>\n                <n-button type=\"primary\" :loading=\"memLoading\" @click=\"runMemoryTest\">运行测试</n-button>\n              </n-space>\n            </n-card>\n            <n-card v-if=\"memResult\" title=\"结果\" size=\"small\" style=\"margin-top: 12px\">\n              <n-descriptions :column=\"2\">\n                <n-descriptions-item label=\"内存占用\">{{ memResult.memoryMB.toFixed(2) }} MB</n-descriptions-item>\n                <n-descriptions-item label=\"分配耗时\">{{ memResult.allocationTimeMs }} ms</n-descriptions-item>\n              </n-descriptions>\n            </n-card>\n          </n-gi>\n        </n-grid>\n      </n-tab-pane>\n\n      <!-- OpenAI-compatible API Test -->\n      <n-tab-pane name=\"api-compare\" tab=\"API 对比测试\">\n        <n-card title=\"API 配置\">\n          <n-space vertical>\n            <n-grid :cols=\"4\" :x-gap=\"20\">\n              <n-gi>\n                <n-form-item label=\"Base URL\">\n                  <n-input v-model:value=\"remoteApi.baseUrl\" placeholder=\"http://localhost:11434\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"remoteApi.apiKey\" type=\"password\" placeholder=\"留空则不发送\" show-password-on=\"click\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"remoteApi.model\" placeholder=\"bge-m3\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"批量大小\">\n                  <n-input-number v-model:value=\"remoteApi.batchSize\" :min=\"1\" :max=\"128\" style=\"width: 100%\" @blur=\"saveApiConfig\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"apiTesting\" :disabled=\"!remoteApi.baseUrl\" @click=\"testApiConnection\">\n                测试连接\n              </n-button>\n              <n-tag v-if=\"apiTestResult\" :type=\"apiTestResult.success ? 'success' : 'error'\">\n                {{ apiTestResult.message }}\n                <template v-if=\"apiTestResult.latency\"> ({{ apiTestResult.latency }}ms)</template>\n              </n-tag>\n            </n-space>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"remoteLoading\" :disabled=\"!remoteApi.baseUrl\" @click=\"runRemoteTest\">\n                运行全部测试（100 项）\n              </n-button>\n              <n-button :loading=\"remoteLoading && remoteActiveCategory !== null\" :disabled=\"!remoteApi.baseUrl\" @click=\"runRemoteAllCategories\">\n                按分类依次测试\n              </n-button>\n            </n-space>\n            <n-text depth=\"3\">支持任意 OpenAI 兼容接口：Ollama、vLLM、Xinference、OpenAI 等。配置自动保存。</n-text>\n          </n-space>\n        </n-card>\n\n        <!-- Category Buttons -->\n        <n-card title=\"分类测试\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"cat in semanticCategories\"\n              :key=\"cat.label\"\n              :loading=\"remoteLoading && remoteActiveCategory === cat.label\"\n              size=\"small\"\n              :type=\"remoteActiveCategory === cat.label ? 'primary' : 'default'\"\n              @click=\"runRemoteCategoryTest(cat)\"\n            >\n              {{ cat.label }}（{{ cat.cases.length }}）\n            </n-button>\n          </n-space>\n        </n-card>\n\n        <!-- Results -->\n        <n-card v-if=\"remoteResults.length > 0\" title=\"测试结果\" style=\"margin-top: 12px\">\n          <template #header-extra>\n            <n-tag type=\"info\" size=\"small\">{{ remoteApi.model }}</n-tag>\n          </template>\n          <!-- Summary -->\n          <n-space style=\"margin-bottom: 16px\">\n            <n-tag type=\"success\" size=\"large\">\n              通过: {{ remoteOverallPassRate.passed }} / {{ remoteOverallPassRate.total }}（{{ remoteOverallPassRate.rate }}）\n            </n-tag>\n            <n-tag v-if=\"remoteMae !== null\" size=\"large\">\n              MAE: {{ remoteMae.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"remoteRmse !== null\" size=\"large\">\n              RMSE: {{ remoteRmse.toFixed(3) }}\n            </n-tag>\n            <n-tag v-if=\"remoteCorrelation !== null\" size=\"large\">\n              相关性: {{ remoteCorrelation.toFixed(3) }}\n            </n-tag>\n            <n-tag size=\"large\">\n              耗时: {{ remoteTotalTime }}ms\n            </n-tag>\n          </n-space>\n\n          <!-- Category Stats -->\n          <n-data-table\n            :columns=\"remoteCategoryColumns\"\n            :data=\"remoteCategoryStats\"\n            :bordered=\"false\"\n            size=\"small\"\n            style=\"margin-bottom: 16px\"\n          />\n\n          <!-- Detail Table -->\n          <n-data-table\n            :columns=\"remoteDetailColumns\"\n            :data=\"remoteResults\"\n            :bordered=\"false\"\n            size=\"small\"\n            :max-height=\"400\"\n            :scroll-x=\"800\"\n          />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- API Functionality Test -->\n      <n-tab-pane name=\"api-test\" tab=\"API 功能测试\">\n        <n-card title=\"第三方 OpenAI 兼容 API 测试\">\n          <n-space vertical>\n            <n-text depth=\"3\">测试任意 OpenAI 兼容接口的功能和性能，验证向量维度、响应时间、批量处理能力等。</n-text>\n\n            <n-grid :cols=\"4\" :x-gap=\"16\">\n              <n-gi>\n                <n-form-item label=\"Base URL\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.baseUrl\" placeholder=\"http://localhost:11434\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"API Key\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.apiKey\" type=\"password\" placeholder=\"可选\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"模型名称\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.model\" placeholder=\"bge-m3\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"测试文本\" :show-feedback=\"false\">\n                  <n-input v-model:value=\"apiTest.testText\" placeholder=\"输入测试文本\" />\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n\n            <n-space>\n              <n-button type=\"primary\" :loading=\"apiFuncTesting\" @click=\"runApiFunctionTest\">\n                运行功能测试\n              </n-button>\n              <n-button :loading=\"apiFuncTesting\" @click=\"runApiBatchTest\">\n                批量性能测试\n              </n-button>\n              <n-button @click=\"loadPresetConfig('lmstudio')\">LM Studio</n-button>\n              <n-button @click=\"loadPresetConfig('ollama')\">Ollama</n-button>\n              <n-button @click=\"loadPresetConfig('openai')\">OpenAI</n-button>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"apiFuncResult\" title=\"测试结果\" style=\"margin-top: 12px\">\n          <n-descriptions :column=\"3\" label-placement=\"left\">\n            <n-descriptions-item label=\"状态\">\n              <n-tag :type=\"apiFuncResult.success ? 'success' : 'error'\">\n                {{ apiFuncResult.success ? '成功' : '失败' }}\n              </n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"向量维度\">{{ apiFuncResult.dimension || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"响应时间\">{{ apiFuncResult.latencyMs || '-' }} ms</n-descriptions-item>\n            <n-descriptions-item label=\"模型\">{{ apiFuncResult.model || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"Token 数量\">{{ apiFuncResult.tokenCount || '-' }}</n-descriptions-item>\n            <n-descriptions-item label=\"批量支持\">{{ apiFuncResult.batchSupported ? '是' : '未知' }}</n-descriptions-item>\n          </n-descriptions>\n\n          <n-divider>向量采样（前10维）</n-divider>\n          <n-code v-if=\"apiFuncResult.embeddingSample\" :code=\"apiFuncResult.embeddingSample\" language=\"json\" />\n\n          <n-divider v-if=\"apiFuncResult.batchResults\">批量测试结果</n-divider>\n          <n-data-table v-if=\"apiFuncResult.batchResults\" :columns=\"apiBatchColumns\" :data=\"apiFuncResult.batchResults\" :bordered=\"false\" size=\"small\" />\n        </n-card>\n\n        <n-card title=\"连接历史\" size=\"small\" style=\"margin-top: 12px\">\n          <n-data-table :columns=\"apiHistoryColumns\" :data=\"apiHistory\" :bordered=\"false\" size=\"small\" :max-height=\"200\" />\n        </n-card>\n      </n-tab-pane>\n\n      <!-- Rerank Model Test -->\n      <n-tab-pane name=\"rerank\" tab=\"重排模型测试\">\n        <n-card>\n          <n-space vertical>\n            <n-text depth=\"3\">测试重排模型的文档排序能力：给定查询和文档列表，模型返回按相关性排序的结果。支持 NDCG、MRR、MAP 等评估指标。</n-text>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"rerankLoading\" @click=\"runAllRerankPresets\">\n                运行全部预设测试\n              </n-button>\n              <n-button @click=\"showCustomRerank = true\">\n                自定义测试\n              </n-button>\n              <n-divider vertical />\n              <n-checkbox v-model:checked=\"useRemoteForRerank\" :disabled=\"!remoteApi.baseUrl\">\n                使用 OpenAI 兼容 API\n              </n-checkbox>\n              <n-tag v-if=\"useRemoteForRerank && remoteApi.model\" type=\"info\" size=\"small\">\n                {{ remoteApi.model }}\n              </n-tag>\n            </n-space>\n          </n-space>\n        </n-card>\n\n        <!-- Preset Test Buttons -->\n        <n-card title=\"预设测试套件\" size=\"small\" style=\"margin-top: 12px\">\n          <n-space>\n            <n-button\n              v-for=\"preset in rerankPresets\"\n              :key=\"preset.name\"\n              :loading=\"rerankLoading && activeRerankPreset === preset.name\"\n              size=\"small\"\n              :type=\"activeRerankPreset === preset.name ? 'primary' : 'default'\"\n              @click=\"runRerankPreset(preset.name)\"\n            >\n              {{ preset.description }}（{{ preset.documentCount }} 文档）\n            </n-button>\n          </n-space>\n        </n-card>\n\n        <!-- Custom Rerank Test Dialog -->\n        <n-modal v-model:show=\"showCustomRerank\" preset=\"dialog\" title=\"自定义重排测试\" style=\"width: 60vw\">\n          <n-space vertical>\n            <n-form-item label=\"查询文本\">\n              <n-input v-model:value=\"rerankQuery\" placeholder=\"输入查询文本\" />\n            </n-form-item>\n            <n-form-item label=\"模型名称\">\n              <n-input v-model:value=\"rerankModelName\" placeholder=\"留空使用默认\" />\n            </n-form-item>\n            <n-form-item label=\"文档列表 (JSON)\">\n              <n-input\n                v-model:value=\"rerankDocumentsJson\"\n                type=\"textarea\"\n                :rows=\"10\"\n                placeholder='[{\"id\":\"1\",\"text\":\"文档内容\",\"expectedRelevance\":0.9}]'\n                style=\"font-family: monospace\"\n              />\n            </n-form-item>\n            <n-text depth=\"3\" style=\"font-size: 12px\">\n              格式: [{\"id\": \"1\", \"text\": \"文档内容\", \"expectedRelevance\": 0.9}]，expectedRelevance 为 0-1 的期望相关性分数\n            </n-text>\n            <n-space>\n              <n-button type=\"primary\" :loading=\"rerankLoading\" @click=\"runCustomRerankTest\">\n                运行测试\n              </n-button>\n              <n-button @click=\"loadSampleRerankDocuments\">\n                加载示例数据\n              </n-button>\n            </n-space>\n          </n-space>\n        </n-modal>\n\n        <!-- Rerank Statistics -->\n        <n-card v-if=\"rerankStatistics\" title=\"测试统计\" style=\"margin-top: 16px\">\n          <n-grid :cols=\"6\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"总测试数\" :value=\"rerankStatistics.totalTests\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 NDCG\" :value=\"rerankStatistics.byModel[0].avgNdcg.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MRR\" :value=\"rerankStatistics.byModel[0].avgMrr.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MAP\" :value=\"rerankStatistics.byModel[0].avgMap.toFixed(3)\" />\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均耗时\" :value=\"rerankStatistics.byModel[0].avgQueryMs.toFixed(0)\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi v-if=\"rerankStatistics.byModel.length > 0\">\n              <n-statistic label=\"平均 MAE\" :value=\"rerankStatistics.byModel[0].avgMae.toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n        </n-card>\n\n        <n-card v-if=\"rerankResult\" title=\"重排结果\" style=\"margin-top: 16px\">\n          <!-- Metrics -->\n          <n-grid :cols=\"5\" :x-gap=\"16\" style=\"margin-bottom: 16px\">\n            <n-gi>\n              <n-statistic label=\"NDCG\" :value=\"(rerankResult.ndcg || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MRR\" :value=\"(rerankResult.mrr || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MAP\" :value=\"(rerankResult.map || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"排序准确度\" :value=\"(rerankResult.rankingAccuracy || 0).toFixed(3)\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"MAE\" :value=\"(rerankResult.meanAbsoluteError || 0).toFixed(3)\" />\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"2\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"查询耗时\" :value=\"rerankResult.queryMs || 0\" />\n              <n-text depth=\"3\">ms</n-text>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"模型\" :value=\"rerankResult.modelName || '-'\" />\n            </n-gi>\n          </n-grid>\n\n          <!-- Document Ranking -->\n          <n-divider>文档排序</n-divider>\n          <n-data-table\n            :columns=\"rerankDocColumns\"\n            :data=\"rerankResult.documents || []\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n        </n-card>\n\n        <!-- Performance Benchmark -->\n        <n-card title=\"性能基准测试\" size=\"small\" style=\"margin-top: 16px\">\n          <n-space vertical>\n            <n-text depth=\"3\">测试重排模型的吞吐量和延迟分布</n-text>\n            <n-grid :cols=\"4\" :x-gap=\"16\">\n              <n-gi>\n                <n-form-item label=\"并发数\">\n                  <n-input-number v-model:value=\"benchmarkConcurrency\" :min=\"1\" :max=\"32\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"总请求数\">\n                  <n-input-number v-model:value=\"benchmarkTotalRequests\" :min=\"10\" :max=\"1000\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"每请求文档数\">\n                  <n-input-number v-model:value=\"benchmarkDocCount\" :min=\"1\" :max=\"20\" style=\"width: 100%\" />\n                </n-form-item>\n              </n-gi>\n              <n-gi>\n                <n-form-item label=\"操作\">\n                  <n-button type=\"primary\" :loading=\"rerankBenchmarkLoading\" @click=\"runRerankBenchmark\">\n                    开始测试\n                  </n-button>\n                </n-form-item>\n              </n-gi>\n            </n-grid>\n          </n-space>\n        </n-card>\n\n        <n-card v-if=\"rerankBenchmarkResult\" title=\"性能测试结果\" style=\"margin-top: 12px\">\n          <n-grid :cols=\"5\" :x-gap=\"16\">\n            <n-gi>\n              <n-statistic label=\"QPS\" :value=\"rerankBenchmarkResult.qps.toFixed(2)\">\n                <template #suffix>req/s</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"平均延迟\" :value=\"rerankBenchmarkResult.avgLatencyMs.toFixed(1)\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P50 延迟\" :value=\"rerankBenchmarkResult.p50LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P95 延迟\" :value=\"rerankBenchmarkResult.p95LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"P99 延迟\" :value=\"rerankBenchmarkResult.p99LatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n          <n-grid :cols=\"4\" :x-gap=\"16\" style=\"margin-top: 12px\">\n            <n-gi>\n              <n-statistic label=\"总耗时\" :value=\"rerankBenchmarkResult.totalMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"成功/失败\" :value=\"`${rerankBenchmarkResult.successCount}/${rerankBenchmarkResult.failCount}`\" />\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最小延迟\" :value=\"rerankBenchmarkResult.minLatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n            <n-gi>\n              <n-statistic label=\"最大延迟\" :value=\"rerankBenchmarkResult.maxLatencyMs\">\n                <template #suffix>ms</template>\n              </n-statistic>\n            </n-gi>\n          </n-grid>\n          <n-alert v-if=\"rerankBenchmarkResult.errors.length > 0\" type=\"warning\" style=\"margin-top: 12px\" title=\"错误信息\">\n            <ul style=\"margin: 0; padding-left: 20px\">\n              <li v-for=\"(err, i) in rerankBenchmarkResult.errors\" :key=\"i\">{{ err }}</li>\n            </ul>\n          </n-alert>\n        </n-card>\n      </n-tab-pane>\n    </n-tabs>\n  </n-space>\n";
export default component;
import {ragFetch as fetch} from '/core/transport/index.js';
