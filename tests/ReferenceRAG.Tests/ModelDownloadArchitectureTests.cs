using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Coordinator;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;
using Xunit;

namespace ReferenceRAG.Tests;

public class ModelDownloadArchitectureTests
{
    [Fact]
    public void DownloadStateMachine_InitialState_IsIdle()
    {
        var sm = new DownloadStateMachine("test-model", 1000);
        Assert.Equal(DownloadStage.Idle, sm.CurrentStage);
        Assert.Equal("downloading", sm.CurrentProgress.Status);
        Assert.Equal(0, sm.CurrentProgress.Progress);
    }

    [Fact]
    public void DownloadStateMachine_TransitionChain_UpdatesStagesCorrectly()
    {
        var recordedStages = new List<DownloadStage>();
        var reporter = new Progress<DownloadProgress>(p => recordedStages.Add(p.Stage));
        var sm = new DownloadStateMachine("test-model", 1000, reporter);

        sm.TransitionTo(DownloadStage.ResolvingManifest, "解析中...");
        Assert.Equal(DownloadStage.ResolvingManifest, sm.CurrentStage);

        sm.TransitionTo(DownloadStage.DownloadingFiles, "下载中...");
        Assert.Equal(DownloadStage.DownloadingFiles, sm.CurrentStage);

        sm.TransitionTo(DownloadStage.VerifyingFiles, "校验中...");
        Assert.Equal(DownloadStage.VerifyingFiles, sm.CurrentStage);

        sm.SetCompleted("完成");
        Assert.Equal(DownloadStage.Completed, sm.CurrentStage);
        Assert.Equal("completed", sm.CurrentProgress.Status);
        Assert.Equal(100f, sm.CurrentProgress.Progress);
    }

    [Fact]
    public void DownloadStateMachine_UpdateFileProgress_SmoothsSpeedAndEta()
    {
        var sm = new DownloadStateMachine("test-model", 10_000_000);

        // Simulate 2MB transferred
        sm.UpdateFileProgress("model.onnx", 1, 3, 2_000_000, 5_000_000, 2_000_000);

        var progress = sm.CurrentProgress;
        Assert.Equal("model.onnx", progress.CurrentFileName);
        Assert.Equal(1, progress.CurrentFileIndex);
        Assert.Equal(3, progress.TotalFiles);
        Assert.Equal(2_000_000, progress.BytesReceived);
        Assert.Equal(10_000_000, progress.TotalBytes);
        Assert.True(progress.Progress >= 20f);
        Assert.Contains("model.onnx", progress.StageMessage);
    }

    [Fact]
    public void DownloadStateMachine_SetFailed_RecordsErrorDetails()
    {
        var sm = new DownloadStateMachine("test-model", 1000);
        sm.SetFailed("网络超时", DownloadErrorCode.NetworkError);

        var p = sm.CurrentProgress;
        Assert.Equal(DownloadStage.Failed, p.Stage);
        Assert.Equal("failed", p.Status);
        Assert.Equal(DownloadErrorCode.NetworkError, p.ErrorCode);
        Assert.Equal("网络超时", p.ErrorMessage);
    }

    [Fact]
    public void DownloadStateMachine_SetCancelled_SetsCancelledState()
    {
        var sm = new DownloadStateMachine("test-model", 1000);
        sm.SetCancelled();

        var p = sm.CurrentProgress;
        Assert.Equal(DownloadStage.Cancelled, p.Stage);
        Assert.Equal("cancelled", p.Status);
    }

    [Fact]
    public void DirectOnnxDownloadStrategy_CanHandle_WhenManifestHasOnnx()
    {
        var strategy = new DirectOnnxDownloadStrategy();
        var manifestWithOnnx = new ModelManifest
        {
            ModelId = "test/onnx-model",
            Files = new List<ModelFileItem>
            {
                new() { Path = "model.onnx", Size = 500_000 },
                new() { Path = "config.json", Size = 1_000 }
            }
        };

        var manifestWithoutOnnx = new ModelManifest
        {
            ModelId = "test/pytorch-model",
            Files = new List<ModelFileItem>
            {
                new() { Path = "model.safetensors", Size = 500_000 }
            }
        };

        var context = new DownloadContext();
        Assert.True(strategy.CanHandle(manifestWithOnnx, context));
        Assert.False(strategy.CanHandle(manifestWithoutOnnx, context));
    }

    [Fact]
    public void DirectGgufDownloadStrategy_CanHandle_WhenManifestHasGguf()
    {
        var strategy = new DirectGgufDownloadStrategy();
        var manifest = new ModelManifest
        {
            ModelId = "test/gguf-model",
            Files = new List<ModelFileItem>
            {
                new() { Path = "model-q4_k_m.gguf", Size = 1_000_000 }
            }
        };

        var context = new DownloadContext();
        Assert.True(strategy.CanHandle(manifest, context));
    }

    [Fact]
    public void CommunityMirrorDownloadStrategy_CanHandle_OnlyWhenNoOnnxAndNoGguf()
    {
        var strategy = new CommunityMirrorDownloadStrategy();
        var pytorchManifest = new ModelManifest
        {
            ModelId = "Alibaba-NLP/gte-multilingual-reranker-base",
            Files = new List<ModelFileItem>
            {
                new() { Path = "model.safetensors", Size = 600_000_000 },
                new() { Path = "config.json", Size = 2_000 }
            }
        };

        var context = new DownloadContext();
        Assert.True(strategy.CanHandle(pytorchManifest, context));
    }

    [Fact]
    public async Task ModelDownloadCoordinator_GetDownloadOptions_ResolvesGgufCandidate()
    {
        var mockAdapter = new MockSourceAdapter();
        // 模拟主仓库仅为 pytorch，而官方 GGUF 仓库包含 gguf 文件
        mockAdapter.RegisterManifest("Qwen/Qwen3-Embedding-0.6B", new ModelManifest
        {
            ModelId = "Qwen/Qwen3-Embedding-0.6B",
            Files = new() { new() { Path = "model.safetensors", Size = 1_000_000_000 } }
        });
        mockAdapter.RegisterManifest("Qwen/Qwen3-Embedding-0.6B-GGUF", new ModelManifest
        {
            ModelId = "Qwen/Qwen3-Embedding-0.6B-GGUF",
            Files = new()
            {
                new() { Path = "qwen3-0.6b-q4_k_m.gguf", Size = 450_000_000 },
                new() { Path = "tokenizer.json", Size = 2_000_000 }
            }
        });

        var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => mockAdapter);
        var options = await coordinator.GetDownloadOptionsAsync("Qwen/Qwen3-Embedding-0.6B", mockAdapter);

        Assert.NotNull(options);
        Assert.True(options.HasGguf);
        Assert.False(options.NeedsConversion);
        Assert.NotEmpty(options.AllOptions);
        Assert.Equal("gguf", options.AllOptions[0].Format);
        Assert.NotNull(options.RecommendedOption);
    }

    [Fact]
    public async Task ModelDownloadCoordinator_GetDownloadOptions_DetectsGatedModel()
    {
        var mockAdapter = new MockSourceAdapter();
        mockAdapter.RegisterUnauthorized("xCloudinfo/xVITA-Embed-300M-zhTW");

        var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => mockAdapter);
        var options = await coordinator.GetDownloadOptionsAsync("xCloudinfo/xVITA-Embed-300M-zhTW", mockAdapter);

        Assert.NotNull(options);
        Assert.True(options.NeedsConversion);
        Assert.True(options.IsGatedOrUnauthorized);
    }

    [Fact]
    public async Task ModelDownloadCoordinator_GetDownloadOptions_ResolvesBothGgufAndOnnx_WhenBothPresent()
    {
        var mockAdapter = new MockSourceAdapter();
        // 主仓库有 ONNX
        mockAdapter.RegisterManifest("BAAI/bge-m3", new ModelManifest
        {
            ModelId = "BAAI/bge-m3",
            Files = new()
            {
                new() { Path = "model.onnx", Size = 2_200_000_000 },
                new() { Path = "config.json", Size = 2_000 }
            }
        });
        // 社区镜像仓库有 GGUF
        mockAdapter.RegisterManifest("BAAI/bge-m3-GGUF", new ModelManifest
        {
            ModelId = "BAAI/bge-m3-GGUF",
            Files = new()
            {
                new() { Path = "bge-m3-q4_k_m.gguf", Size = 600_000_000 }
            }
        });

        var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => mockAdapter);
        var options = await coordinator.GetDownloadOptionsAsync("BAAI/bge-m3", mockAdapter);

        Assert.NotNull(options);
        Assert.True(options.HasOnnx);
        Assert.True(options.HasGguf);
        Assert.True(options.HasBothFormats);
        Assert.NotEmpty(options.OnnxOptions);
        Assert.NotEmpty(options.GgufOptions);
        Assert.True(options.AllOptions.Count >= 2);
        Assert.NotNull(options.RecommendedOption);
    }

    [Fact]
    public void DirectStrategies_YieldWhenOtherFormatRequested()
    {
        var onnxStrategy = new DirectOnnxDownloadStrategy();
        var ggufStrategy = new DirectGgufDownloadStrategy();

        var dualManifest = new ModelManifest
        {
            ModelId = "test/dual-model",
            Files = new()
            {
                new() { Path = "model.onnx", Size = 1_000_000 },
                new() { Path = "model.gguf", Size = 500_000 }
            }
        };

        // 当请求 GGUF 时，ONNX 策略主动让位
        var ggufContext = new DownloadContext { TargetFormat = "gguf" };
        Assert.False(onnxStrategy.CanHandle(dualManifest, ggufContext));
        Assert.True(ggufStrategy.CanHandle(dualManifest, ggufContext));

        // 当请求 ONNX 时，GGUF 策略主动让位
        var onnxContext = new DownloadContext { TargetFormat = "onnx" };
        Assert.True(onnxStrategy.CanHandle(dualManifest, onnxContext));
        Assert.False(ggufStrategy.CanHandle(dualManifest, onnxContext));
    }

    [Fact(Skip = "需要网络访问 HuggingFace API")]
    public async Task HuggingFaceSourceAdapter_GetManifest_ResolvesPublicModel()
    {
        using var adapter = new HuggingFaceSourceAdapter();
        var manifest = await adapter.GetManifestAsync("Alibaba-NLP/gte-multilingual-reranker-base");

        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest.Files);
        Assert.Contains(manifest.Files, f => f.Path == "config.json");
        Assert.Contains(manifest.Files, f => f.Path == "model.safetensors");
    }

    [Fact(Skip = "需要网络访问 HuggingFace API")]
    public async Task ModelDownloadCoordinator_GetDownloadOptions_ForCommunityOnnxModel()
    {
        var coordinator = new ModelDownloadCoordinator();
        var options = await coordinator.GetDownloadOptionsAsync("onnx-community/gte-multilingual-reranker-base");

        Assert.NotNull(options);
        Assert.True(options.HasOnnx);
        Assert.False(options.NeedsConversion);
        Assert.NotEmpty(options.AllOptions);
        Assert.NotNull(options.RecommendedOption);
    }

    private class MockSourceAdapter : IModelSourceAdapter
    {
        private readonly Dictionary<string, ModelManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unauthorized = new(StringComparer.OrdinalIgnoreCase);

        public string SourceName => "Mock";
        public string ActiveEndpoint => "https://mock";

        public void RegisterManifest(string modelId, ModelManifest manifest) => _manifests[modelId] = manifest;
        public void RegisterUnauthorized(string modelId) => _unauthorized.Add(modelId);

        public Task<ModelManifest> GetManifestAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (_unauthorized.Contains(modelId))
            {
                throw new UnauthorizedAccessException($"模型 '{modelId}' 属于受限授权模型 (HTTP 401)");
            }

            if (_manifests.TryGetValue(modelId, out var m))
            {
                return Task.FromResult(m);
            }

            throw new KeyNotFoundException($"Model not found: {modelId}");
        }

        public Task<long> ResolveFileSizeAsync(string modelId, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(1024L);

        public Task DownloadFileAsync(string modelId, string relativePath, string localDestinationPath, Action<long, long>? onChunkReceived = null, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(localDestinationPath, "mock");
            onChunkReceived?.Invoke(4, 4);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
