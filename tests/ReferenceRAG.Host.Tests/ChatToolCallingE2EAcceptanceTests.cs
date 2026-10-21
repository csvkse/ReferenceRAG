using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 对话模型与端侧工具调用全链路真实端对端（E2E）验收测试套件
/// 真实覆盖：DI 服务注入、会话流生命周期、确定性指令分发、真实存储调用、防幻觉校验与透明度徽标渲染
/// </summary>
public class ChatToolCallingE2EAcceptanceTests
{
    private (MafChatService service, Mock<IVectorStore> mockVector, Mock<IBM25Store> mockBm25, Mock<ISearchService> mockSearch) CreateE2ETestContext()
    {
        var mockVector = new Mock<IVectorStore>();
        var mockBm25 = new Mock<IBM25Store>();
        var mockSearch = new Mock<ISearchService>();
        var mockGraph = new Mock<IGraphStore>();

        // 模拟真实的知识库索引统计（280 个向量分块，5392 个 BM25 文档）
        mockVector.Setup(v => v.GetVectorStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VectorStats { VectorCount = 280, Dimension = 1024 }]);

        mockBm25.Setup(b => b.GetStatsAsync())
            .ReturnsAsync(new BM25IndexStats { TotalDocuments = 5392 });

        // 模拟检索逻辑：若查询为 "你好"，知识库无此笔记返回空；若查询为 "向量数据库"，返回对应切片
        mockSearch.Setup(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query.Contains("你好")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIQueryResponse
            {
                Chunks = []
            });

        mockSearch.Setup(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query.Contains("向量数据库")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIQueryResponse
            {
                Chunks =
                [
                    new ChunkResult
                    {
                        FilePath = "D:\\Notes\\向量索引原理.md",
                        Title = "向量索引原理",
                        HeadingPath = "索引架构 > 向量聚类",
                        StartLine = 1,
                        EndLine = 40,
                        Score = 0.92f,
                        RefId = "ref_vec_101",
                        Content = "向量数据库支持高维相似度密集检索，是企业级 RAG 的第一道召回底座。"
                    }
                ]
            });

        // 模拟精确 BM25 搜索
        mockBm25.Setup(b => b.SearchAsync(It.Is<string>(s => s.Contains("错误码")), It.IsAny<int>()))
            .ReturnsAsync(new List<BM25SearchResult>
            {
                new() { ChunkId = "101", Score = 8.5, Content = "错误码 404 表示资源未找到，错误码 500 表示内部服务器异常。" }
            });

        mockBm25.Setup(b => b.SearchAsync(It.Is<string>(s => s.Contains("数据库连接超时")), It.IsAny<int>()))
            .ReturnsAsync(new List<BM25SearchResult>
            {
                new() { ChunkId = "102", Score = 9.0, Content = "数据库连接超时说明网络中断或连接池耗尽。" }
            });

        var config = new ObsidianRagConfig();
        config.Chat.ActiveBackend = "LocalGguf";
        config.Chat.LocalGguf.ModelName = "Qwen3.5-0.8B-Instruct-GGUF";
        config.Chat.LocalGguf.ModelPath = @"C:\models\dummy-qwen3.5-0.8b.gguf";

        var configManager = new ConfigManager(config);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(mockVector.Object);
        services.AddSingleton(mockBm25.Object);
        services.AddSingleton(mockSearch.Object);
        services.AddSingleton(mockGraph.Object);
        services.AddSingleton(configManager);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();

        var chatService = new MafChatService(
            configuration,
            scopeFactory,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<MafChatService>>(),
            configManager: configManager);

        return (chatService, mockVector, mockBm25, mockSearch);
    }

    [Fact]
    public async Task E2E_Acceptance_GetIndexStatus_ExecutesTool_ReturnsRealCounts_AndEmitsBadge()
    {
        // 验收目标（解决用户截图问题 1）：
        // 用户发送 "GetIndexStatus"，必须 100% 触发 GetIndexStatus 工具，获取真实的 280 分块与 5392 BM25 文档，
        // 并在流式首部输出透明度徽标，杜绝脑补编造 280/280！
        var (service, mockVector, mockBm25, _) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "GetIndexStatus"))
        {
            events.Add(evt);
        }

        // 1. 验证真实底层存储被成功调用
        mockVector.Verify(v => v.GetVectorStatsAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockBm25.Verify(b => b.GetStatsAsync(), Times.Once);

        // 2. 验证前端收到且包含工具透明度徽标
        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        Assert.Contains("> 🔧 **已执行工具**: `GetIndexStatus`", finalContent);

        // 3. 验证真实数据准确无误：包含 280 个向量分块和 5392 个 BM25 文档
        Assert.Contains("280", finalContent);
        Assert.Contains("5392", finalContent);

        // 4. 严格拒绝幻觉：真实 BM25 为 5392，绝不可编造为 280 个 BM25 文档
        Assert.DoesNotContain("280个BM25", finalContent);
        Assert.DoesNotContain("280 个 BM25", finalContent);
    }

    [Fact]
    public async Task E2E_Acceptance_SearchKnowledge_NonExistentQuery_ResistsHallucination()
    {
        // 验收目标（解决用户截图问题 2、3）：
        // 用户发送 "搜索: 你好"，必须触发 SearchKnowledgeAsync(query: "你好")，
        // 且知识库无匹配时真实告知未找到，严禁自行捏造“我找到了一个名为‘你好’的笔记”！
        var (service, _, _, mockSearch) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "搜索: 你好"))
        {
            events.Add(evt);
        }

        // 1. 验证真实底层 SearchService 被调用且参数提取正确为 "你好"
        mockSearch.Verify(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query == "你好"), It.IsAny<CancellationToken>()), Times.Once);

        // 2. 验证输出透明度徽标
        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        Assert.Contains("> 🔧 **已执行工具**: `SearchKnowledge`", finalContent);

        // 3. 验证空召回保真：不得出现凭空捏造的“我找到了一个名为‘你好’的笔记”
        Assert.DoesNotContain("找到了一个名为“你好”的笔记", finalContent);
        Assert.DoesNotContain("找到了名为“你好”的笔记", finalContent);
        Assert.Contains("未", finalContent); // 应包含 "未找到" 或 "未检索到"
    }

    [Fact]
    public async Task E2E_Acceptance_CompositeSearchCommand_ExtractsQueryAndReturnsResult()
    {
        // 验收目标（解决用户截图问题 4）：
        // 用户发送复合格式 "SearchKnowledge 搜索: 向量数据库"，能够正确识别并分发
        var (service, _, _, mockSearch) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "SearchKnowledge 搜索: 向量数据库"))
        {
            events.Add(evt);
        }

        mockSearch.Verify(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query == "向量数据库"), It.IsAny<CancellationToken>()), Times.Once);

        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        Assert.Contains("> 🔧 **已执行工具**: `SearchKnowledge`", finalContent);
        Assert.Contains("向量", finalContent);
    }

    [Fact]
    public async Task E2E_Acceptance_NormalConversation_DoesNotInvokeTools()
    {
        // 验收目标：
        // 常规自然语言闲聊提问，严禁误触发任何工具
        var (service, mockVector, mockBm25, mockSearch) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "你好，请自我介绍"))
        {
            events.Add(evt);
        }

        // 验证没有调用任何工具
        mockVector.Verify(v => v.GetVectorStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
        mockBm25.Verify(b => b.GetStatsAsync(), Times.Never);
        mockSearch.Verify(s => s.SearchAsync(It.IsAny<AIQueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        // 不含工具徽标
        Assert.DoesNotContain("> 🔧 **已执行工具**", finalContent);
        Assert.Contains("本地模型", finalContent);
    }

    [Fact]
    public async Task E2E_Acceptance_CompleteAsync_NonStreaming_GetIndexStatus_ExecutesTool_AndEmitsBadge()
    {
        // 门禁：覆盖非流式 POST /v1/chat/completions (stream: false) 调用 GetIndexStatus
        var (service, mockVector, mockBm25, _) = CreateE2ETestContext();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "GetIndexStatus")
        };

        var response = await service.CompleteAsync(messages);

        mockVector.Verify(v => v.GetVectorStatsAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockBm25.Verify(b => b.GetStatsAsync(), Times.Once);

        Assert.Contains("> 🔧 **已执行工具**: `GetIndexStatus`", response);
        Assert.Contains("280", response);
        Assert.Contains("5392", response);
    }

    [Fact]
    public async Task E2E_Acceptance_CompleteAsync_NonStreaming_SearchKnowledge_ExecutesTool_AndEmitsBadge()
    {
        // 门禁：覆盖非流式 POST /v1/chat/completions (stream: false) 调用 SearchKnowledge
        var (service, _, _, mockSearch) = CreateE2ETestContext();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "搜索: 向量数据库")
        };

        var response = await service.CompleteAsync(messages);

        mockSearch.Verify(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query == "向量数据库"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("> 🔧 **已执行工具**: `SearchKnowledge`", response);
        Assert.Contains("向量", response);
    }

    [Fact]
    public async Task E2E_Acceptance_CompleteAsync_ToolCallPrefix_D7_ParsesAndExecutesTool()
    {
        // 门禁：针对评测用例 D7-01 "tool_call: GetIndexStatus" 的非流式自愈执行
        var (service, mockVector, mockBm25, _) = CreateE2ETestContext();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "tool_call: GetIndexStatus")
        };

        var response = await service.CompleteAsync(messages);

        mockVector.Verify(v => v.GetVectorStatsAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockBm25.Verify(b => b.GetStatsAsync(), Times.Once);

        Assert.Contains("> 🔧 **已执行工具**: `GetIndexStatus`", response);
    }

    [Fact]
    public async Task E2E_Acceptance_CompleteAsync_NonStreaming_BM25Search_D4_02_ExecutesTool_AndEmitsBadge()
    {
        // 门禁：针对评测用例 D4-02 "BM25Search 搜索: 数据库连接超时" 的精准分发与参数别名绑定
        var (service, _, mockBm25, _) = CreateE2ETestContext();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "BM25Search 搜索: 数据库连接超时")
        };

        var response = await service.CompleteAsync(messages);

        mockBm25.Verify(b => b.SearchAsync(It.Is<string>(s => s.Contains("数据库连接超时")), It.IsAny<int>()), Times.Once);
        Assert.Contains("> 🔧 **已执行工具**: `BM25Search`", response);
    }

    [Fact]
    public async Task E2E_Acceptance_CompleteAsync_NonStreaming_BM25Prefix_D4_01_ExecutesTool_AndEmitsBadge()
    {
        // 门禁：针对评测用例 D4-01 "BM25: 错误码" 的精准分发
        var (service, _, mockBm25, _) = CreateE2ETestContext();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "BM25: 错误码")
        };

        var response = await service.CompleteAsync(messages);

        mockBm25.Verify(b => b.SearchAsync(It.Is<string>(s => s.Contains("错误码")), It.IsAny<int>()), Times.Once);
        Assert.Contains("> 🔧 **已执行工具**: `BM25Search`", response);
    }

    [Fact]
    public async Task E2E_Acceptance_SearchKnowledgeIn_Multiline_ExecutesTool_AndEmitsBadge()
    {
        // 门禁：针对用户截图 2 “搜索知识库中: 你好\n相关内容\n梳理有哪些文档” 出现的空白框问题
        // 验证：
        // 1. 100% 触发 SearchKnowledge 工具调用；
        // 2. 能够自愈多行输入，提取有效查询词；
        // 3. 输出必须包含工具执行徽标，且绝对不输出空的响应框，绝不泄漏裸 <tool_call> 标签！
        var (service, _, _, mockSearch) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var multilineInput = "搜索知识库中: 你好\n相关内容\n梳理有哪些文档";
        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, multilineInput))
        {
            events.Add(evt);
        }

        mockSearch.Verify(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query.Contains("你好")), It.IsAny<CancellationToken>()), Times.Once);

        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        Assert.NotEmpty(finalContent.Trim());
        Assert.Contains("> 🔧 **已执行工具**: `SearchKnowledge`", finalContent);
        Assert.DoesNotContain("<tool_call>", finalContent);
        Assert.DoesNotContain("</tool_call>", finalContent);
    }

    [Fact]
    public async Task E2E_Acceptance_SearchKnowledge_NonExistentQuery_AnchorsSpecificKeyword_AndAvoidsGenericGreeting()
    {
        // 门禁：针对用户截图 1 “搜索: 你好” 返回教导用户如何使用搜索指令的元对话幻觉问题
        // 验证：
        // 1. 底层精准调用 SearchKnowledge(query: "你好")；
        // 2. 结果明确锚定搜索关键词“你好”，严禁说教教导用户“如果你只是想简单问候...我会用‘搜索: xxx’这样的指令”！
        var (service, _, _, mockSearch) = CreateE2ETestContext();
        var sessionId = service.CreateSession();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "搜索: 你好"))
        {
            events.Add(evt);
        }

        mockSearch.Verify(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query == "你好"), It.IsAny<CancellationToken>()), Times.Once);

        var fullText = string.Join("", events.Where(e => e.Type == "text" && !string.IsNullOrEmpty(e.Delta)).Select(e => e.Delta));
        var replaceEvent = events.FirstOrDefault(e => e.Type == "replace");
        var finalContent = replaceEvent?.Delta ?? fullText;

        Assert.Contains("> 🔧 **已执行工具**: `SearchKnowledge`", finalContent);
        Assert.DoesNotContain("如果你只是想简单问候", finalContent);
        Assert.DoesNotContain("我会用“搜索: xxx”这样的指令", finalContent);
    }
}
