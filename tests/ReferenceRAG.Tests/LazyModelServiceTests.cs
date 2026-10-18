using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Tests;

public class LazyModelServiceTests
{
    [Fact]
    public async Task EmbeddingModelIsNotCreatedUntilInference()
    {
        var created=0;
        using var service=new LazyEmbeddingService(new EmbeddingOptions{ModelPath="missing.onnx",ModelName="test"},null,()=>
        {
            created++;
            return new EmbeddingService(new EmbeddingOptions{ModelPath="missing.onnx",ModelName="test"});
        });

        Assert.Equal("test",service.ModelName);
        service.Normalize([3,4]);
        Assert.False(service.IsModelLoaded);
        Assert.Equal(0,created);

        await service.EncodeAsync("first inference");
        Assert.True(service.IsModelLoaded);
        Assert.Equal(1,created);
    }

    [Fact]
    public async Task RerankModelIsNotCreatedUntilInference()
    {
        var created=0;
        using var service=new LazyRerankService("test",()=>
        {
            created++;
            return new OnnxRerankService(new RerankOptions{ModelPath="missing.onnx",ModelName="test"});
        });

        Assert.Equal("test",service.ModelName);
        Assert.False(service.IsLoaded);
        Assert.False(service.IsModelLoaded);
        Assert.Equal(0,created);

        await service.RerankAsync("query","document");
        Assert.True(service.IsModelLoaded);
        Assert.Equal(1,created);
    }

    [Fact]
    public async Task LazyEmbeddingService_ReloadModelAsync_SwitchesToGgufModel_Successfully()
    {
        var realGgufPath = @"E:\LinuxWork\Obsidian\resource\data\models\Embedding\Qwen3-Embedding-0.6B\Qwen3-Embedding-0.6B-Q8_0.gguf";
        if (!File.Exists(realGgufPath)) return;

        using var service = new LazyEmbeddingService(new EmbeddingOptions
        {
            ModelPath = "missing.onnx",
            ModelName = "initial-model",
            UseCuda = true
        }, null);

        var success = await service.ReloadModelAsync(realGgufPath, "Qwen3-Embedding-0.6B", 8192);

        Assert.True(success, "LazyEmbeddingService 切换到 GGUF 模型应返回成功");
        Assert.Equal("Qwen3-Embedding-0.6B", service.ModelName);
        Assert.Equal(1024, service.Dimension);
        Assert.False(service.IsSimulationMode);

        var vector = await service.EncodeAsync("测试文本");
        Assert.NotNull(vector);
        Assert.Equal(1024, vector.Length);
    }

    [Fact]
    public void WindowStateService_TogglesStateAndNotifiesSubscribers()
    {
        var windowState = new WindowStateService();
        Assert.False(windowState.IsMinimizedToTray);

        var events = new List<bool>();
        windowState.MinimizedToTrayChanged += (_, state) => events.Add(state);

        windowState.SetMinimizedToTray(true);
        Assert.True(windowState.IsMinimizedToTray);
        Assert.Single(events);
        Assert.True(events[0]);

        // 重复调用相同状态不应重复触发事件
        windowState.SetMinimizedToTray(true);
        Assert.Single(events);

        windowState.SetMinimizedToTray(false);
        Assert.False(windowState.IsMinimizedToTray);
        Assert.Equal(2, events.Count);
        Assert.False(events[1]);
    }

    [Fact]
    public async Task PermanentRetentionStrategy_NeverUnloadsOnIdle()
    {
        var windowState = new WindowStateService();
        using var service = new LazyEmbeddingService(
            new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" },
            null,
            () => new EmbeddingService(new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" }),
            windowState);

        // 明确设置为固定常驻模式
        service.UpdateRetentionPolicy(ModelRetentionStrategy.Permanent, 1, false);

        await service.EncodeAsync("query text");
        Assert.True(service.IsModelLoaded);

        // 即使等待超过 1 秒，模型仍保持常驻未卸载
        await Task.Delay(1200);
        Assert.True(service.IsModelLoaded);
    }

    [Fact]
    public async Task SlidingExpiration_WhenInForeground_DoesNotUnload_IfUnloadOnlyWhenMinimizedToTrayIsTrue()
    {
        var windowState = new WindowStateService();
        windowState.SetMinimizedToTray(false); // 窗口在前台打开

        using var service = new LazyEmbeddingService(
            new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" },
            null,
            () => new EmbeddingService(new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" }),
            windowState);

        // 1 秒短超时，仅托盘生效开启
        service.UpdateRetentionPolicy(ModelRetentionStrategy.SlidingExpiration, 1, true);

        await service.EncodeAsync("query text");
        Assert.True(service.IsModelLoaded);

        // 前台打开状态下，达到超时时间坚决不卸载（保持常驻）
        await Task.Delay(1300);
        Assert.True(service.IsModelLoaded, "前台打开使用期间不得卸载模型");

        // 用户将窗口最小化到系统托盘
        windowState.SetMinimizedToTray(true);

        // 托盘中空闲超时后，自动触发卸载释放显存
        await Task.Delay(1300);
        Assert.False(service.IsModelLoaded, "托盘后台空闲超时应自动卸载模型释放显存");
    }

    [Fact]
    public async Task SlidingExpiration_CancelIdleTimer_WhenRestoredToForeground()
    {
        var windowState = new WindowStateService();
        windowState.SetMinimizedToTray(true); // 初始在托盘中

        using var service = new LazyEmbeddingService(
            new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" },
            null,
            () => new EmbeddingService(new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test" }),
            windowState);

        // 2 秒超时，仅托盘生效
        service.UpdateRetentionPolicy(ModelRetentionStrategy.SlidingExpiration, 2, true);

        await service.EncodeAsync("query text");
        Assert.True(service.IsModelLoaded);

        // 等待 1 秒（未达 2 秒超时），此时用户从托盘恢复窗口到前台！
        await Task.Delay(1000);
        windowState.SetMinimizedToTray(false);

        // 再等待 1.5 秒（累计 2.5 秒 > 2 秒超时阈值）
        await Task.Delay(1500);

        // 模型必须仍然处于加载就绪状态（因为恢复前台取消了滑动过期计时器）
        Assert.True(service.IsModelLoaded, "恢复前台应取消滑动超时倒计时，模型保持常驻");
    }
}
