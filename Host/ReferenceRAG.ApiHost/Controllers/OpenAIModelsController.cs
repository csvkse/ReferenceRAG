using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// OpenAI 兼容的 Models 发现接口
/// 供第三方软件（Obsidian Copilot, Cherry Studio, NextChat, Chatbox, Cursor 等）检索可用模型
/// 输出严格遵循 OpenAI camelCase 格式
/// </summary>
[ApiController]
public class OpenAIModelsController : ControllerBase
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankService _rerankService;
    private readonly MafChatService _chatService;
    private readonly IModelManager? _modelManager;
    private readonly ICatalogStore? _catalogStore;
    private readonly ILogger<OpenAIModelsController> _logger;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIModelsController(
        IEmbeddingService embeddingService,
        IRerankService rerankService,
        MafChatService chatService,
        ILogger<OpenAIModelsController> logger,
        IModelManager? modelManager = null,
        ICatalogStore? catalogStore = null)
    {
        _embeddingService = embeddingService;
        _rerankService = rerankService;
        _chatService = chatService;
        _logger = logger;
        _modelManager = modelManager;
        _catalogStore = catalogStore;
    }

    /// <summary>
    /// GET /v1/models
    /// 列出当前已激活可用的模型（包括 RAG 增强模型、激活 Chat 模型、激活 Embedding 模型、激活 Rerank 模型以及多库隔离虚拟模型）。
    /// 如需列出本地已下载的全部未激活模型，可指定 ?include_all=true。
    /// </summary>
    [HttpGet("v1/models")]
    public ActionResult GetModels([FromQuery(Name = "include_all")] bool includeAll = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelList = new List<object>
        {
            new
            {
                id = "reference-rag",
                @object = "model",
                created = now,
                owned_by = "reference-rag",
                description = "ReferenceRAG 知识库混合检索增强生成助手 (Hybrid RAG Assistant)"
            }
        };

        // 活跃知识库虚拟模型（支持 kb:<kbId> 直接路由）
        if (_catalogStore != null)
        {
            try
            {
                var kbs = _catalogStore.ListKnowledgeBasesAsync().GetAwaiter().GetResult();
                foreach (var kb in kbs)
                {
                    modelList.Add(new
                    {
                        id = $"kb:{kb.Id}",
                        @object = "model",
                        created = now,
                        owned_by = "reference-rag",
                        description = $"Knowledge Base: {kb.Name} ({kb.Description})"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "列出知识库模型列表失败");
            }
        }

        // 活跃聊天模型（当前就绪）
        var currentChat = !string.IsNullOrWhiteSpace(_chatService.CurrentModel)
            ? _chatService.CurrentModel
            : "gpt-4o-mini";

        modelList.Add(new
        {
            id = currentChat,
            @object = "model",
            created = now,
            owned_by = "reference-rag"
        });

        // 活跃向量嵌入模型（当前就绪）
        if (!string.IsNullOrWhiteSpace(_embeddingService.ModelName))
        {
            modelList.Add(new
            {
                id = _embeddingService.ModelName,
                @object = "model",
                created = now,
                owned_by = "reference-rag"
            });
        }

        // 活跃重排模型（当前就绪）
        if (!string.IsNullOrWhiteSpace(_rerankService.ModelName))
        {
            modelList.Add(new
            {
                id = _rerankService.ModelName,
                @object = "model",
                created = now,
                owned_by = "reference-rag"
            });
        }

        // 可选：仅在显式请求 ?include_all=true 时追加本地已下载但未激活的模型
        if (includeAll && _modelManager != null)
        {
            try
            {
                var downloadedChatModels = _modelManager.GetDownloadedChatModels();
                foreach (var dm in downloadedChatModels)
                {
                    if (!modelList.Any(m => (string)((dynamic)m).id == dm.Name))
                    {
                        modelList.Add(new
                        {
                            id = dm.Name,
                            @object = "model",
                            created = now,
                            owned_by = "reference-rag"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "检索本地已下载聊天模型失败");
            }
        }

        return CamelCaseJson(new
        {
            @object = "list",
            data = modelList
        });
    }

    /// <summary>
    /// GET /v1/models/{modelId}
    /// 获取指定模型的元数据
    /// </summary>
    [HttpGet("v1/models/{modelId}")]
    public ActionResult GetModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return CamelCaseJson(new { error = new { message = "Model id cannot be empty", type = "invalid_request_error" } }, 400);

        return CamelCaseJson(new
        {
            id = modelId,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "reference-rag"
        });
    }

    private ContentResult CamelCaseJson(object value, int statusCode = 200)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        return Content(JsonSerializer.Serialize(value, CamelCase), "application/json");
    }
}
