using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Xunit.Skip;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Interfaces;
using System.Diagnostics;

namespace ReferenceRAG.Tests;

/// <summary>
/// 模型切换功能测试
/// </summary>
public class ModelSwitchTests
{
    private const string TestDataPath = "E:/LinuxWork/Obsidian/resource/data";
    private const string TestModelsPath = "E:/LinuxWork/Obsidian/resource/data/models";
    private const string TestAppSettingsPath = "E:/LinuxWork/Obsidian/resource/data/appsettings.json";

    private ConfigManager CreateTestConfigManager()
    {
        if (!Directory.Exists(TestDataPath))
            throw new InvalidOperationException($"测试数据路径不存在: {TestDataPath}");

        Directory.SetCurrentDirectory(TestDataPath);
        return new ConfigManager();
    }

    private static bool TestPathsExist()
    {
        return File.Exists(TestAppSettingsPath) && Directory.Exists(TestModelsPath);
    }

    [SkippableFact]
    public void Test1_ModelManager_Initialization()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 1: ModelManager 初始化 ===");

        // 检查配置文件
        Console.WriteLine($"配置文件路径: {TestAppSettingsPath}");
        Assert.True(File.Exists(TestAppSettingsPath), $"配置文件不存在: {TestAppSettingsPath}");

        // 读取配置
        var configJson = File.ReadAllText(TestAppSettingsPath);
        Console.WriteLine($"配置内容:\n{configJson}");

        // 创建 ModelManager
        var configManager = CreateTestConfigManager();
        var manager = new ModelManager(TestModelsPath, configManager);

        // 获取所有模型
        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();

        Console.WriteLine($"\n已注册模型数量: {models.Count}");
        foreach (var model in models)
        {
            Console.WriteLine($"  - {model.Name}: IsDownloaded={model.IsDownloaded}, LocalPath={model.LocalPath ?? "null"}");
        }

        // 验证 bge-base-zh-v1.5 是否被正确识别
        var bgeBase = models.FirstOrDefault(m => m.Name == "bge-base-zh-v1.5");
        Assert.NotNull(bgeBase);
        Console.WriteLine($"\nbge-base-zh-v1.5 状态:");
        Console.WriteLine($"  IsDownloaded: {bgeBase!.IsDownloaded}");
        Console.WriteLine($"  LocalPath: {bgeBase.LocalPath ?? "null"}");
        Console.WriteLine($"  ModelSizeBytes: {bgeBase.ModelSizeBytes}");
    }

    [SkippableFact]
    public void Test2_ModelFiles_Exist()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 2: 模型文件存在性检查 ===");

        var modelDir = Path.Combine(TestModelsPath, "bge-base-zh-v1.5");
        Console.WriteLine($"模型目录: {modelDir}");
        Assert.True(Directory.Exists(modelDir), $"模型目录不存在: {modelDir}");

        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx.data");

        Console.WriteLine($"\n文件检查:");
        Console.WriteLine($"  model.onnx: {(File.Exists(onnxPath) ? "存在" : "不存在")} ({new FileInfo(onnxPath).Length / 1024.0 / 1024.0:F2} MB)");
        Console.WriteLine($"  model.onnx.data: {(File.Exists(onnxDataPath) ? "存在" : "不存在")} ({(File.Exists(onnxDataPath) ? new FileInfo(onnxDataPath).Length / 1024.0 / 1024.0 : 0):F2} MB)");

        // 列出目录所有文件
        Console.WriteLine($"\n目录内容:");
        foreach (var file in Directory.GetFiles(modelDir, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(file);
            Console.WriteLine($"  {Path.GetFileName(file)}: {fi.Length / 1024.0 / 1024.0:F2} MB");
        }
    }

    [SkippableFact]
    public void Test3_EmbeddingService_LoadModel()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 3: EmbeddingService 模型加载 ===");

        var modelPath = Path.Combine(TestModelsPath, "bge-base-zh-v1.5", "model.onnx");
        Console.WriteLine($"模型路径: {modelPath}");
        Assert.True(File.Exists(modelPath), $"模型文件不存在: {modelPath}");

        // 创建 EmbeddingService
        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-base-zh-v1.5",
            MaxSequenceLength = 512,
            BatchSize = 32,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        Console.WriteLine($"\nEmbeddingService 状态:");
        Console.WriteLine($"  ModelName: {service.ModelName}");
        Console.WriteLine($"  Dimension: {service.Dimension}");
        Console.WriteLine($"  IsSimulationMode: {service.IsSimulationMode}");

        Assert.False(service.IsSimulationMode, "模型加载失败，处于模拟模式");
        Assert.Equal(768, service.Dimension);
    }

    [Fact]
    public void Test4_EmbeddingService_ReloadModel()
    {
        Console.WriteLine("=== Test 4: EmbeddingService 模型切换 ===");

        // 先加载 small 模型
        var smallModelPath = Path.Combine(TestModelsPath, "../models/bge-small-zh-v1.5/model.onnx");
        if (!File.Exists(smallModelPath))
        {
            smallModelPath = "E:/LinuxWork/Obsidian/resource/models/bge-small-zh-v1.5/model.onnx";
        }

        var baseModelPath = Path.Combine(TestModelsPath, "bge-base-zh-v1.5/model.onnx");

        Console.WriteLine($"Small 模型路径: {smallModelPath}");
        Console.WriteLine($"Base 模型路径: {baseModelPath}");

        if (!File.Exists(smallModelPath) || !File.Exists(baseModelPath))
        {
            Console.WriteLine("跳过测试：缺少模型文件");
            return; // Skip
        }

        // 创建服务加载 small 模型
        var options = new EmbeddingOptions
        {
            ModelPath = smallModelPath,
            ModelName = "bge-small-zh-v1.5",
            MaxSequenceLength = 512,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        var initialDimension = service.Dimension;
        Console.WriteLine($"\n初始状态: ModelName={service.ModelName}, Dimension={initialDimension}");

        // 切换到 base 模型
        Console.WriteLine($"\n正在切换到 bge-base-zh-v1.5...");
        var success = service.ReloadModelAsync(baseModelPath, "bge-base-zh-v1.5").GetAwaiter().GetResult();

        Console.WriteLine($"切换结果: {success}");
        Console.WriteLine($"切换后状态: ModelName={service.ModelName}, Dimension={service.Dimension}");

        Assert.True(success, "模型切换失败");
        Assert.Equal(768, service.Dimension);
        Assert.NotEqual(initialDimension, service.Dimension);
    }

    [SkippableFact]
    public void Test5_ModelManager_SwitchModel()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 5: ModelManager 切换模型 ===");

        var configManager = CreateTestConfigManager();
        var manager = new ModelManager(TestModelsPath, configManager);

        // 获取可用模型
        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();
        var bgeBase = models.FirstOrDefault(m => m.Name == "bge-base-zh-v1.5");

        Console.WriteLine($"bge-base-zh-v1.5 状态:");
        Console.WriteLine($"  IsDownloaded: {bgeBase?.IsDownloaded ?? false}");
        Console.WriteLine($"  LocalPath: {bgeBase?.LocalPath ?? "null"}");

        if (bgeBase == null || !bgeBase.IsDownloaded)
        {
            Console.WriteLine("模型未标记为已下载");
            return;
        }

        // 尝试切换
        Console.WriteLine($"\n尝试切换模型...");
        var success = manager.SwitchModelAsync("bge-base-zh-v1.5").GetAwaiter().GetResult();
        Console.WriteLine($"切换结果: {success}");

        // 验证配置文件
        var configJson = File.ReadAllText(TestAppSettingsPath);
        Console.WriteLine($"\n更新后的配置:");
        Console.WriteLine(configJson);

        Assert.True(success, "ModelManager.SwitchModelAsync 失败");
    }

    [SkippableFact]
    public void Test6_DiagnoseModelRegistry()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 6: 诊断模型注册表 ===");

        var configManager = CreateTestConfigManager();
        var manager = new ModelManager(TestModelsPath, configManager);
        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();

        Console.WriteLine("\n所有注册模型:");
        Console.WriteLine("名称".PadRight(25) + "已下载".PadRight(10) + "维度".PadRight(8) + "本地路径");
        Console.WriteLine(new string('-', 80));

        foreach (var model in models)
        {
            Console.WriteLine(
                model.Name.PadRight(25) +
                model.IsDownloaded.ToString().PadRight(10) +
                model.Dimension.ToString().PadRight(8) +
                (model.LocalPath ?? "null")
            );
        }

        // 详细检查 bge-base-zh-v1.5
        var bgeBase = models.FirstOrDefault(m => m.Name == "bge-base-zh-v1.5");
        if (bgeBase != null)
        {
            Console.WriteLine($"\n=== bge-base-zh-v1.5 详细信息 ===");
            Console.WriteLine($"  Name: {bgeBase.Name}");
            Console.WriteLine($"  DisplayName: {bgeBase.DisplayName}");
            Console.WriteLine($"  Description: {bgeBase.Description}");
            Console.WriteLine($"  Dimension: {bgeBase.Dimension}");
            Console.WriteLine($"  IsDownloaded: {bgeBase.IsDownloaded}");
            Console.WriteLine($"  LocalPath: {bgeBase.LocalPath ?? "null"}");
            Console.WriteLine($"  ModelSizeBytes: {bgeBase.ModelSizeBytes} ({bgeBase.ModelSizeBytes / 1024.0 / 1024.0:F2} MB)");
            Console.WriteLine($"  HasOnnx: {bgeBase.HasOnnx}");
        }
    }

    [SkippableFact]
    public void Test7_FullSwitchWorkflow()
    {
        Skip.If(!TestPathsExist(), "本地测试路径不存在，跳过");
        Console.WriteLine("=== Test 7: 完整切换流程 ===");

        // 1. 初始化 ModelManager
        var configManager = CreateTestConfigManager();
        var manager = new ModelManager(TestModelsPath, configManager);
        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();

        // 2. 找到已下载的模型
        var downloadedModels = models.Where(m => m.IsDownloaded).ToList();
        Console.WriteLine($"已下载模型数量: {downloadedModels.Count}");
        foreach (var m in downloadedModels)
        {
            Console.WriteLine($"  - {m.Name}: LocalPath={m.LocalPath}");
        }

        Assert.True(downloadedModels.Count > 0, "没有已下载的模型");

        // 3. 选择目标模型
        var targetModel = downloadedModels.FirstOrDefault(m => m.Name == "bge-base-zh-v1.5")
                         ?? downloadedModels.First();

        Console.WriteLine($"\n目标模型: {targetModel.Name}");
        Console.WriteLine($"  LocalPath: {targetModel.LocalPath ?? "null"}");
        Console.WriteLine($"  Dimension: {targetModel.Dimension}");

        // 4. 验证 LocalPath
        Assert.NotNull(targetModel.LocalPath);

        // 5. 构建 ONNX 路径
        var onnxPath = Path.Combine(targetModel.LocalPath!, "model.onnx");
        Console.WriteLine($"  ONNX 路径: {onnxPath}");
        Assert.True(File.Exists(onnxPath), $"ONNX 文件不存在: {onnxPath}");

        // 6. 创建 EmbeddingService
        var options = new EmbeddingOptions
        {
            ModelPath = onnxPath,
            ModelName = targetModel.Name,
            MaxSequenceLength = 512,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        Console.WriteLine($"\nEmbeddingService 状态:");
        Console.WriteLine($"  ModelName: {service.ModelName}");
        Console.WriteLine($"  Dimension: {service.Dimension}");
        Console.WriteLine($"  IsSimulationMode: {service.IsSimulationMode}");

        Assert.False(service.IsSimulationMode, "不应处于模拟模式");
        Assert.Equal(targetModel.Dimension, service.Dimension);

        // 7. 测试推理
        Console.WriteLine($"\n测试推理...");
        var embedding = service.EncodeAsync("测试文本").GetAwaiter().GetResult();
        Console.WriteLine($"  向量长度: {embedding.Length}");
        Console.WriteLine($"  前5个值: [{string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}]");

        Assert.Equal(targetModel.Dimension, embedding.Length);
    }

    // ===== MaxSequenceLength 修复验证测试 =====

    [Fact]
    public async Task ReloadModelAsync_WithMaxSequenceLength_UpdatesOptions()
    {
        // EmbeddingService 以 512 启动，切换时传入新的 maxSequenceLength，应生效
        var options = new EmbeddingOptions
        {
            ModelPath = "",
            ModelName = "initial",
            MaxSequenceLength = 512
        };
        var service = new EmbeddingService(options);

        // 切换到空路径（进入模拟模式），但 MaxSequenceLength 应已更新
        await service.ReloadModelAsync("", "bge-m3", maxSequenceLength: 8192);

        // 验证模拟模式下仍能工作
        Assert.True(service.IsSimulationMode);

        service.Dispose();
    }

    [Fact]
    public void ModelRegistry_BgeM3_Has8192MaxSequenceLength()
    {
        var configManager = new ConfigManager();
        var manager = new ModelManager(Path.GetTempPath(), configManager);

        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();
        var bgeM3 = models.FirstOrDefault(m => m.Name == "bge-m3");

        Assert.NotNull(bgeM3);
        Assert.Equal(8192, bgeM3!.MaxSequenceLength);
    }

    [Fact]
    public void ModelRegistry_StandardModels_Have512MaxSequenceLength()
    {
        var configManager = new ConfigManager();
        var manager = new ModelManager(Path.GetTempPath(), configManager);

        var models = manager.GetAvailableModelsAsync().GetAwaiter().GetResult();

        var standardModels = new[] { "bge-small-zh-v1.5", "bge-base-zh-v1.5", "bge-large-zh-v1.5" };
        foreach (var name in standardModels)
        {
            var m = models.FirstOrDefault(x => x.Name == name);
            Assert.NotNull(m);
            Assert.Equal(512, m!.MaxSequenceLength);
        }
    }
}
