using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Services;

/// <summary>
/// 解析后的模型资产执行凭据与元数据
/// </summary>
public sealed class ResolvedModelAsset
{
    public ModelUrn Urn { get; set; } = ModelUrn.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ModelType { get; set; } = "embedding"; // "chat" | "embedding" | "rerank"
    public bool IsLocal { get; set; }
    public bool IsReady { get; set; }
    public string? StatusReason { get; set; }

    // 本地特有执行参数
    public string? LocalModelPath { get; set; }
    public string? LocalVariant { get; set; }
    public string? LocalFormat { get; set; } // "onnx" | "gguf"

    // 云端特有执行参数
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? RemoteModelId { get; set; }

    // 通用规格
    public int Dimension { get; set; }
    public int MaxTokens { get; set; } = 8192;
}

public sealed class ModelCatalogOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public string Details { get; set; } = string.Empty;
    public bool Disabled { get; set; }
}

public sealed class ModelCatalogGroup
{
    public string Label { get; set; } = string.Empty;
    public List<ModelCatalogOption> Options { get; set; } = new();
}

public sealed class ModelCatalogResponse
{
    public string ModelType { get; set; } = string.Empty;
    public List<ModelCatalogGroup> Groups { get; set; } = new();
}

public interface IModelAssetResolver
{
    ResolvedModelAsset? Resolve(string? modelUrn, string expectedType);
    ModelCatalogResponse GetCatalog(string? modelType = null);
}

public sealed class ModelAssetResolver : IModelAssetResolver
{
    private readonly ConfigManager _configManager;
    private readonly IModelManager? _modelManager;
    private readonly ILogger<ModelAssetResolver> _logger;

    public ModelAssetResolver(
        ConfigManager configManager,
        IModelManager? modelManager = null,
        ILogger<ModelAssetResolver>? logger = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _modelManager = modelManager;
        _logger = logger ?? NullLogger<ModelAssetResolver>.Instance;
    }

    public ResolvedModelAsset? Resolve(string? inputUrn, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(inputUrn)) return null;
        var urn = ModelUrn.Parse(inputUrn);
        if (urn.IsEmpty) return null;

        var config = _configManager.Load();

        if (urn.IsProvider)
        {
            var prov = config.ModelProviders?.FirstOrDefault(p => string.Equals(p.Id, urn.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (prov == null)
            {
                return new ResolvedModelAsset
                {
                    Urn = urn,
                    DisplayName = $"{urn.ProviderId} / {urn.ModelId}",
                    ModelType = expectedType,
                    IsLocal = false,
                    IsReady = false,
                    StatusReason = $"服务商「{urn.ProviderId}」不存在或已被移除"
                };
            }

            var mod = prov.Models?.FirstOrDefault(m => string.Equals(m.ModelId, urn.ModelId, StringComparison.OrdinalIgnoreCase));
            var isReady = prov.Enabled && (mod?.Enabled ?? true);
            var reason = !prov.Enabled ? "服务商已被停用" : (mod != null && !mod.Enabled ? "该模型已被停用" : "就绪");

            return new ResolvedModelAsset
            {
                Urn = urn,
                DisplayName = mod?.DisplayName ?? $"{prov.Name} / {urn.ModelId}",
                ModelType = mod?.ModelType ?? expectedType,
                IsLocal = false,
                IsReady = isReady,
                StatusReason = reason,
                ProviderId = prov.Id,
                ProviderName = prov.Name,
                BaseUrl = prov.BaseUrl,
                ApiKey = prov.ApiKey,
                RemoteModelId = urn.ModelId,
                Dimension = mod?.Dimension ?? 1024,
                MaxTokens = mod?.MaxTokens ?? 8192
            };
        }

        if (urn.IsLocal)
        {
            if (_modelManager == null)
            {
                return new ResolvedModelAsset
                {
                    Urn = urn,
                    DisplayName = urn.ModelName,
                    ModelType = expectedType,
                    IsLocal = true,
                    IsReady = false,
                    StatusReason = "本地模型管理器未就绪"
                };
            }

            ModelInfo? localModel = null;
            if (expectedType.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                localModel = _modelManager.GetChatModels()?.FirstOrDefault(m => string.Equals(m.Name, urn.ModelName, StringComparison.OrdinalIgnoreCase));
            }
            else if (expectedType.Equals("rerank", StringComparison.OrdinalIgnoreCase) || expectedType.Equals("reranker", StringComparison.OrdinalIgnoreCase))
            {
                localModel = _modelManager.GetRerankModels()?.FirstOrDefault(m => string.Equals(m.Name, urn.ModelName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                localModel = _modelManager.GetEmbeddingModels()?.FirstOrDefault(m => string.Equals(m.Name, urn.ModelName, StringComparison.OrdinalIgnoreCase));
            }

            if (localModel == null)
            {
                return new ResolvedModelAsset
                {
                    Urn = urn,
                    DisplayName = urn.ModelName,
                    ModelType = expectedType,
                    IsLocal = true,
                    IsReady = false,
                    StatusReason = $"本地模型「{urn.ModelName}」未注册或未下载"
                };
            }

            var activeVariant = localModel.InstalledVariants?.FirstOrDefault(v => v.IsActive) ?? localModel.InstalledVariants?.FirstOrDefault();
            var resolvedPath = activeVariant?.FullPath;
            if (string.IsNullOrEmpty(resolvedPath) && !string.IsNullOrEmpty(localModel.LocalPath))
            {
                resolvedPath = ModelManager.ResolveModelFilePath(localModel.LocalPath, activeVariant?.FileName);
            }

            var isReady = localModel.IsDownloaded && localModel.PhysicalExists && localModel.IsEnabled;
            var reason = !localModel.IsDownloaded ? "未下载" : (!localModel.PhysicalExists ? "权重文件缺失" : (!localModel.IsEnabled ? "已被停用" : "就绪"));

            return new ResolvedModelAsset
            {
                Urn = urn,
                DisplayName = localModel.DisplayName ?? localModel.Name,
                ModelType = localModel.ModelType ?? expectedType,
                IsLocal = true,
                IsReady = isReady,
                StatusReason = reason,
                LocalModelPath = resolvedPath,
                LocalVariant = activeVariant?.FileName,
                LocalFormat = activeVariant?.Format ?? (expectedType.Equals("chat", StringComparison.OrdinalIgnoreCase) ? "gguf" : "onnx"),
                Dimension = localModel.Dimension > 0 ? localModel.Dimension : 512,
                MaxTokens = localModel.MaxSequenceLength > 0 ? localModel.MaxSequenceLength : 512
            };
        }

        return null;
    }

    public ModelCatalogResponse GetCatalog(string? modelType = null)
    {
        var targetType = (modelType ?? string.Empty).ToLowerInvariant();
        var resp = new ModelCatalogResponse { ModelType = targetType };
        var config = _configManager.Load();

        // 1. 本地模型分组
        var localGroup = new ModelCatalogGroup { Label = "💻 本地模型 (Local Engines)" };
        if (_modelManager != null)
        {
            IEnumerable<ModelInfo> localList = targetType switch
            {
                "chat" => _modelManager.GetChatModels(),
                "rerank" or "reranker" => _modelManager.GetRerankModels(),
                "embedding" => _modelManager.GetEmbeddingModels(),
                _ => _modelManager.GetEmbeddingModels().Concat(_modelManager.GetChatModels()).Concat(_modelManager.GetRerankModels())
            };

            foreach (var m in localList.OrderByDescending(m => m.IsDownloaded && m.PhysicalExists && m.IsEnabled).ThenBy(m => m.Name))
            {
                var isReady = m.IsDownloaded && m.PhysicalExists && m.IsEnabled;
                var details = !m.IsDownloaded ? "未下载" : (!m.PhysicalExists ? "⚠️ 文件缺失" : (!m.IsEnabled ? "已停用" : "✓ 就绪"));
                if (m.Dimension > 0) details = $"{m.Dimension} 维 / {details}";

                localGroup.Options.Add(new ModelCatalogOption
                {
                    Label = m.DisplayName ?? m.Name,
                    Value = ModelUrn.ForLocal(m.Name).ToString(),
                    IsLocal = true,
                    Details = details,
                    Disabled = !isReady
                });
            }
        }

        if (localGroup.Options.Count > 0)
        {
            resp.Groups.Add(localGroup);
        }

        // 2. 云端在线服务商分组 (按 Provider 分组或统一分组)
        if (config.ModelProviders != null)
        {
            foreach (var prov in config.ModelProviders)
            {
                var provGroup = new ModelCatalogGroup
                {
                    Label = $"🌐 在线服务商 ({prov.Name}){(prov.Enabled ? "" : " [已停用]")}"
                };

                if (prov.Models != null)
                {
                    var matchedModels = prov.Models.AsEnumerable();
                    if (!string.IsNullOrEmpty(targetType))
                    {
                        matchedModels = matchedModels.Where(m => string.Equals(m.ModelType, targetType, StringComparison.OrdinalIgnoreCase));
                    }

                    foreach (var mod in matchedModels)
                    {
                        var isReady = prov.Enabled && mod.Enabled;
                        var details = !prov.Enabled ? "服务商已停用" : (!mod.Enabled ? "模型已停用" : "云端 API / ✓ 就绪");
                        if (mod.Dimension > 0) details = $"{mod.Dimension} 维 / {details}";

                        provGroup.Options.Add(new ModelCatalogOption
                        {
                            Label = mod.DisplayName ?? mod.ModelId,
                            Value = ModelUrn.ForProvider(prov.Id, mod.ModelId).ToString(),
                            IsLocal = false,
                            Details = details,
                            Disabled = !isReady
                        });
                    }
                }

                if (provGroup.Options.Count > 0)
                {
                    resp.Groups.Add(provGroup);
                }
            }
        }

        return resp;
    }
}
