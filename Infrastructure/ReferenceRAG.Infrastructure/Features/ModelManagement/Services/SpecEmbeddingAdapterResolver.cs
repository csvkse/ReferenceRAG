using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Features.ModelManagement.Strategies;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Services;

/// <summary>
/// 按目标 EmbeddingSpec 解析并创建真实嵌入适配器。
/// 与全局"当前激活模型"解耦：reindex 必须编码为目标模型的向量，
/// 解析失败/未就绪/模拟模式/维度不符一律抛异常，禁止静默回退。
/// </summary>
public interface IEmbeddingAdapterResolver
{
    Task<IEmbeddingBackendAdapter> CreateForSpecAsync(EmbeddingSpec spec, CancellationToken ct = default);
}

public sealed class SpecEmbeddingAdapterResolver : IEmbeddingAdapterResolver
{
    private readonly IModelAssetResolver _assetResolver;
    private readonly ConfigManager _configManager;
    private readonly ILogger<SpecEmbeddingAdapterResolver>? _logger;

    public SpecEmbeddingAdapterResolver(
        IModelAssetResolver assetResolver,
        ConfigManager configManager,
        ILogger<SpecEmbeddingAdapterResolver>? logger = null)
    {
        _assetResolver = assetResolver ?? throw new ArgumentNullException(nameof(assetResolver));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger;
    }

    public async Task<IEmbeddingBackendAdapter> CreateForSpecAsync(EmbeddingSpec spec, CancellationToken ct = default)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));

        var isApi = string.Equals(spec.IngestionBackend, "OpenAiApi", StringComparison.OrdinalIgnoreCase)
            || spec.Id.StartsWith("prov_", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(spec.IngestionBaseUrl);

        IEmbeddingBackendAdapter adapter = isApi
            ? await CreateApiAdapterAsync(spec, ct)
            : await CreateLocalAdapterAsync(spec, ct);

        try
        {
            if (adapter.IsSimulationMode)
            {
                throw new InvalidOperationException($"目标模型「{spec.ModelName}」处于模拟降级模式，禁止用于重建索引");
            }
            if (spec.Dimension > 0 && adapter.Dimension > 0 && adapter.Dimension != spec.Dimension)
            {
                throw new InvalidOperationException(
                    $"目标模型维度不匹配: 适配器输出 {adapter.Dimension} 维，规范声明 {spec.Dimension} 维 ({spec.Id})");
            }
        }
        catch
        {
            adapter.Dispose();
            throw;
        }

        _logger?.LogInformation(
            "[SpecEmbeddingAdapterResolver] 目标适配器就绪: {SpecId} backend={Backend} dim={Dim}",
            spec.Id, adapter.BackendType, adapter.Dimension);
        return adapter;
    }

    private async Task<IEmbeddingBackendAdapter> CreateApiAdapterAsync(EmbeddingSpec spec, CancellationToken ct)
    {
        var asset = _assetResolver.Resolve(spec.Id, "embedding");
        var baseUrl = !string.IsNullOrWhiteSpace(spec.IngestionBaseUrl) ? spec.IngestionBaseUrl : asset?.BaseUrl;
        var apiKey = !string.IsNullOrWhiteSpace(spec.IngestionApiKey) ? spec.IngestionApiKey : asset?.ApiKey;
        var remoteModelId = asset?.RemoteModelId ?? spec.ModelName;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"目标模型「{spec.Id}」无法解析 API 端点: {asset?.StatusReason ?? "服务商不存在且规范未提供 IngestionBaseUrl"}");
        }
        if (asset != null && !asset.IsReady && string.IsNullOrWhiteSpace(spec.IngestionBaseUrl))
        {
            throw new InvalidOperationException($"目标模型「{spec.Id}」未就绪: {asset.StatusReason}");
        }

        var config = new EmbeddingConfig
        {
            ActiveBackend = "OpenAiApi",
            ModelName = remoteModelId,
            ApiBaseUrl = baseUrl,
            ApiKey = apiKey,
            ApiDimension = spec.Dimension > 0 ? spec.Dimension : (asset?.Dimension ?? 0),
            MaxSequenceLength = spec.MaxTokens > 0 ? spec.MaxTokens : 8192,
            ApiMaxInputTokens = spec.MaxTokens > 0 ? spec.MaxTokens : 8192
        };

        return await new OpenAiBackendStrategy().CreateEmbeddingAdapterAsync(baseUrl, remoteModelId, config, cancellationToken: ct);
    }

    private async Task<IEmbeddingBackendAdapter> CreateLocalAdapterAsync(EmbeddingSpec spec, CancellationToken ct)
    {
        var modelName = spec.Id.StartsWith("local_", StringComparison.OrdinalIgnoreCase)
            ? spec.Id.Substring("local_".Length)
            : spec.ModelName;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException($"目标规范「{spec.Id}」缺少本地模型名称");
        }

        var asset = _assetResolver.Resolve(ModelUrn.ForLocal(modelName).ToString(), "embedding");
        if (asset == null || !asset.IsReady || string.IsNullOrWhiteSpace(asset.LocalModelPath))
        {
            throw new InvalidOperationException(
                $"目标本地模型「{modelName}」未就绪: {asset?.StatusReason ?? "未注册"}");
        }

        var isGguf = string.Equals(asset.LocalFormat, "gguf", StringComparison.OrdinalIgnoreCase);
        var config = new EmbeddingConfig
        {
            ActiveBackend = isGguf ? "LocalGguf" : "LocalOnnx",
            ModelName = modelName,
            ModelPath = asset.LocalModelPath!,
            MaxSequenceLength = spec.MaxTokens > 0 ? spec.MaxTokens : asset.MaxTokens,
            Acceleration = _configManager.Load().Embedding?.Acceleration ?? new HardwareAccelerationConfig()
        };

        var strategy = ModelBackendStrategyFactory.ResolveStrategy(asset.LocalModelPath!, isGguf ? "gguf" : "onnx");
        return await strategy.CreateEmbeddingAdapterAsync(asset.LocalModelPath!, modelName, config, cancellationToken: ct);
    }
}