using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 连接器多态序列化、传输切片防回归与路径探针细粒度状态门禁测试
/// </summary>
public class ConnectorSerializationAndProbeGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBasesController _kbController;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConnectorSerializationAndProbeGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_ConnSerialization_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var catalogDb = Path.Combine(_tempDir, "system", "catalog.db");
        _catalogStore = new CatalogStore(catalogDb);
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();

        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalogStore, _storageManager, modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);
        _kbController = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);
    }

    public void Dispose()
    {
        _catalogStore.Dispose();
        _storageManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // ignored in test cleanup
        }
    }

    [Fact]
    public async Task GetConnectors_SerializationRoundtrip_PreservesAllDetailProperties_WithoutObjectSlicing()
    {
        // 1. 创建物理测试目录与测试知识库
        var sourcesDir = Path.Combine(_tempDir, "sources_smoke");
        Directory.CreateDirectory(sourcesDir);
        File.WriteAllText(Path.Combine(sourcesDir, "sample.md"), "# Title\n\nSample content for testing reachability and chunks.");

        var createKbRes = await _kbController.CreateKnowledgeBase(new CreateKnowledgeBaseRequest(
            Name: "Serialization Gate KB",
            Description: "Gate KB for testing serialization slicing",
            EmbeddingSpecId: "default-spec"
        ), CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createKbRes.Result).Value);

        var addConnRes = await _kbController.AddConnector(
            kb.Id,
            new AddConnectorRequest("Folder", sourcesDir, "*.md"),
            CancellationToken.None);
        var conn = Assert.IsType<ConnectorDefinition>(Assert.IsType<OkObjectResult>(addConnRes.Result).Value);

        // 2. 模拟写入文档与分块
        using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite, CancellationToken.None))
        {
            var store = lease.Store;
            var batch = new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: conn.Id,
                SourceKey: "sample.md",
                Locator: Path.Combine(sourcesDir, "sample.md"),
                SourceRevision: "rev_1",
                ContentHash: "hash_1",
                Chunks: new List<ChunkData>
                {
                    new ChunkData("chunk_1", 0, "Sample chunk content 1", 20),
                    new ChunkData("chunk_2", 1, "Sample chunk content 2", 20)
                }
            );
            await store.CommitDocumentAsync(batch);
        }

        // 3. 执行控制器调用并获取结果
        var actionResult = await _kbController.GetConnectors(kb.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);

        // 4. 【核心防回归断言】执行实际的 System.Text.Json HTTP 响应序列化
        var jsonString = JsonSerializer.Serialize(okResult.Value, _jsonOptions);
        Assert.False(string.IsNullOrWhiteSpace(jsonString));

        // 5. 反序列化为原始 JsonDocument，断言属性决不允许被 Object Slicing 裁切！
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var rootArray = jsonDoc.RootElement;
        Assert.Equal(JsonValueKind.Array, rootArray.ValueKind);
        Assert.True(rootArray.GetArrayLength() > 0, "连接器列表不能为空");

        var firstElement = rootArray[0];

        // 关键字段存在性校验
        Assert.True(firstElement.TryGetProperty("DocumentCount", out var docProp), "JSON 必须包含 DocumentCount 属性");
        Assert.True(firstElement.TryGetProperty("ChunkCount", out var chunkProp), "JSON 必须包含 ChunkCount 属性");
        Assert.True(firstElement.TryGetProperty("IsReachable", out var reachProp), "JSON 必须包含 IsReachable 属性");
        Assert.True(firstElement.TryGetProperty("Reachability", out var stateProp), "JSON 必须包含 Reachability 状态枚举");

        // 关键数值校验
        Assert.Equal(1, docProp.GetInt32());
        Assert.Equal(2, chunkProp.GetInt32());
        Assert.True(reachProp.GetBoolean());
        Assert.Equal("Reachable", stateProp.GetString());
    }

    [Fact]
    public void ConnectorDefinition_PolymorphicBaseSerialization_IncludesDerivedProperties()
    {
        // 验证即使显式声明为基类列表 IReadOnlyList<ConnectorDefinition>，标注了 [JsonDerivedType] 后也能完整保留派生属性
        var detailDto = new ConnectorDetailDto(
            Id: "conn_poly_1",
            KnowledgeBaseId: "kb_1",
            Kind: "Folder",
            TargetPath: "/app/sources/smoke",
            FilePattern: "*.md",
            ConfigRevision: 1,
            Enabled: true,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            DocumentCount: 42,
            ChunkCount: 1024,
            IsReachable: true,
            Reachability: ConnectorReachabilityState.Reachable,
            DiagnosticMessage: null
        );

        IReadOnlyList<ConnectorDefinition> baseList = new ConnectorDefinition[] { detailDto };

        var jsonString = JsonSerializer.Serialize(baseList, _jsonOptions);
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var firstElement = jsonDoc.RootElement[0];

        Assert.True(firstElement.TryGetProperty("DocumentCount", out var docProp));
        Assert.Equal(42, docProp.GetInt32());

        Assert.True(firstElement.TryGetProperty("ChunkCount", out var chunkProp));
        Assert.Equal(1024, chunkProp.GetInt32());

        Assert.True(firstElement.TryGetProperty("IsReachable", out var reachProp));
        Assert.True(reachProp.GetBoolean());

        Assert.True(firstElement.TryGetProperty("Reachability", out var stateProp));
        Assert.Equal("Reachable", stateProp.GetString());
    }

    [Fact]
    public async Task GetConnectors_UnreachableOrInvalidDirectory_ReturnsDiagnosticMessage()
    {
        var createKbRes = await _kbController.CreateKnowledgeBase(new CreateKnowledgeBaseRequest(
            Name: "Unreachable KB",
            Description: "KB with non-existent path",
            EmbeddingSpecId: "default-spec"
        ), CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createKbRes.Result).Value);

        var nonExistentPath = Path.Combine(_tempDir, "this_directory_does_not_exist_xyz");
        var addConnRes = await _kbController.AddConnector(
            kb.Id,
            new AddConnectorRequest("Folder", nonExistentPath, "*.md"),
            CancellationToken.None);
        Assert.IsType<ConnectorDefinition>(Assert.IsType<OkObjectResult>(addConnRes.Result).Value);

        var actionResult = await _kbController.GetConnectors(kb.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);

        var jsonString = JsonSerializer.Serialize(okResult.Value, _jsonOptions);
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var firstElement = jsonDoc.RootElement[0];

        Assert.True(firstElement.TryGetProperty("IsReachable", out var reachProp));
        Assert.False(reachProp.GetBoolean());

        Assert.True(firstElement.TryGetProperty("Reachability", out var stateProp));
        Assert.Equal("Unreachable", stateProp.GetString());

        Assert.True(firstElement.TryGetProperty("DiagnosticMessage", out var diagProp));
        Assert.Equal("目录不存在", diagProp.GetString());
    }
}
