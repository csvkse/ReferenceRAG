using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReferenceRAG.Tests;

/// <summary>
/// G-KB-2 门禁：AI 对话内置工具的 KB 覆盖。
///
/// 背景：对话共 8 个内置工具，其中 SearchKnowledge / BM25Search / SearchNoteByTitle /
/// GetIndexStatus 已适配多库，但 DrillDownChunk / GetFileStructure / ReadFileContent
/// 曾只走旧库 —— 导致"检索到的 KB 切片被截断后推荐 DrillDown，但展开必然失败"。
/// 本门禁锁定：三个工具都必须在签名上暴露 knowledgeBaseId/chunkId/documentId，
/// 且 GetFileStructure 与 ReadFileContent 对 KB 文档给出可执行指引而非"文件不存在"。
/// </summary>
public sealed class ChatKbToolGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storage;
    private readonly KnowledgeBaseService _kbService;

    public ChatKbToolGateTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chat-kb-gate-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_tempDir);
        _catalog = new CatalogStore(System.IO.Path.Combine(_tempDir, "system", "catalog.db"));
        _catalog.InitializeAsync().GetAwaiter().GetResult();
        _storage = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalog, _storage, modelRouter: null,
            logger: NullLogger<KnowledgeBaseService>.Instance);
    }

    public void Dispose()
    {
        _storage.Dispose();
        _catalog.Dispose();
        try { System.IO.Directory.Delete(_tempDir, true); } catch { }
    }

    private static MethodInfo GetToolMethod(string name) =>
        typeof(MafChatService).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(m => m.Name == name);

    [Theory]
    [InlineData("DrillDownChunkAsync", "knowledgeBaseId")]
    [InlineData("DrillDownChunkAsync", "chunkId")]
    public void DrillDownChunk_ExposesKnowledgeBaseParameters(string method, string parameter)
    {
        var m = GetToolMethod(method);
        var names = m.GetParameters().Select(p => p.Name).ToList();
        Assert.Contains(parameter, names);
        // 说明性约束：参数必须带 Description，模型才能理解用法
        var p = m.GetParameters().First(x => x.Name == parameter);
        Assert.NotNull(p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>());
    }

    [Fact]
    public void DrillDownChunk_HasDescriptionOnAllParameters()
    {
        var m = GetToolMethod("DrillDownChunkAsync");
        var missing = m.GetParameters()
            .Where(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is null)
            .Select(p => p.Name)
            .ToList();
        Assert.True(missing.Count == 0, "缺少 Description 的参数：" + string.Join(",", missing));
    }

    [Fact]
    public void ChatTools_AreAllRegisteredWithDescriptions()
    {
        var tools = typeof(MafChatService).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is not null
                        && m.ReturnType == typeof(Task<string>))
            .ToList();
        Assert.True(tools.Count >= 8, $"内置工具数量异常：{tools.Count}");
    }

    [Theory]
    [InlineData("GetFileStructureAsync")]
    [InlineData("DrillDownChunkAsync")]
    public void KnowledgeBaseFallbacks_MustRequireOptionalParameters(string methodName)
    {
        // v2 门禁：这两个工具曾经把「无 KB 场景」变成硬失败或静默空结果，
        // 模型因此自行编造目录树/索引状态。参数必须可缺省，且内部走 KB 分支。
        var m = GetToolMethod(methodName);
        var ps = m.GetParameters();
        var missing = string.Join(",", ps.Where(p => !p.HasDefaultValue).Select(p => p.Name));
        Assert.True(ps.All(p => p.HasDefaultValue), $"{methodName} 的参数必须可缺省（可选）。缺少默认值：{missing}");
    }

    [Fact]
    public void GetFileStructure_WithoutPaths_MustResolveKnowledgeBaseTree()
    {
        var m = GetToolMethod("GetFileStructureAsync");
        Assert.NotNull(m);
        var source = File.ReadAllText(FindMafChatServiceSource());
        Assert.Contains("BuildKnowledgeBaseTreeAsync", source);
        Assert.Contains("GetPathsAsync", source);
    }

    [Fact]
    public void GetIndexStatus_MustReportChunksForLexicalOnlyKnowledgeBase()
    {
        // 纯词法库 ActiveVectorCount 恒为 0，只报向量会把「已就绪」误报成「空」。
        var source = File.ReadAllText(FindMafChatServiceSource());
        var block = source.Substring(source.IndexOf("GetIndexStatusAsync", StringComparison.Ordinal));
        Assert.Contains("GetLexicalStatsAsync", block, StringComparison.Ordinal);
        Assert.Contains("ActiveVectorCount", block, StringComparison.Ordinal);
    }

    private static string FindMafChatServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")))
            {
                var p = Path.Combine(dir.FullName, "Business", "ReferenceRAG.Business", "Features", "Chat", "Services", "MafChatService.cs");
                Assert.True(File.Exists(p), $"未找到 MafChatService.cs: {p}");
                return p;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到代码仓根目录 (ReferenceRAG.slnx)");
    }

    [Fact]
    public async Task KnowledgeBaseDocument_IsResolvableForChatGuidance()
    {
        // 构造一个真实 KB 文档，验证 GetDocumentsAsync(search: fileName) 能命中 locator，
        // 从而让对话工具给出「属于知识库 X」的可执行指引。
        var path = System.IO.Path.Combine(_tempDir, "kbdocs");
        System.IO.Directory.CreateDirectory(path);
        var kb = await _kbService.CreateKnowledgeBaseAsync("指引库", null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var connector = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", path);
        var doc = await _kbService.CreateDocumentAsync(kb.Id, connector.Id, "guide.md", "# 指引\n\n正文内容。");

        var (docs, total) = await _kbService.GetDocumentsAsync(kb.Id, 1, 20, search: "guide.md");
        Assert.True(total >= 1);
        // 手工录入文档的 Locator 是 manual://，索引入库文档才是真实路径；
        // 对话兜底按 source_key 命中后比对 locator，故此处只需验证可被检索到。
        Assert.Contains(docs, d => d.Id == doc.Id && d.SourceKey == "guide.md");
    }
}
