using System;
using System.IO;
using System.Linq;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Features.ModelManagement.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class SiliconFlowAndSystemOneConfigBootstrapTests : IDisposable
{
    private readonly string _tempConfigFile;

    public SiliconFlowAndSystemOneConfigBootstrapTests()
    {
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"cfg_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempConfigFile))
        {
            File.Delete(_tempConfigFile);
        }
    }

    [Fact]
    public void ConfigManager_ApplyEnvironmentVariables_AutoRegisters_SiliconFlowAndSystemOne()
    {
        var origSfKey = Environment.GetEnvironmentVariable("SILICONFLOW_API_KEY");
        var origSfBase = Environment.GetEnvironmentVariable("SILICONFLOW_BASE_URL");
        var origTsKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("SILICONFLOW_API_KEY", "sk-mock-siliconflow-key");
            Environment.SetEnvironmentVariable("SILICONFLOW_BASE_URL", "https://api.siliconflow.cn/v1");
            Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", "apikey_mock_typesafe_key");

            var configManager = new ConfigManager(new ObsidianRagConfig(), _tempConfigFile);
            var config = configManager.Load();

            // 1. 验证 SiliconFlow 服务商及模型资产自动注册
            var sfProv = config.ModelProviders.FirstOrDefault(p => p.Id == "prov_sf");
            Assert.NotNull(sfProv);
            Assert.Equal("SiliconFlow", sfProv.Name);
            Assert.Equal("sk-mock-siliconflow-key", sfProv.ApiKey);
            Assert.Equal("https://api.siliconflow.cn/v1", sfProv.BaseUrl);

            var bgeM3 = sfProv.Models.FirstOrDefault(m => m.ModelId == "BAAI/bge-m3");
            Assert.NotNull(bgeM3);
            Assert.Equal(1024, bgeM3.Dimension);
            Assert.Equal("embedding", bgeM3.ModelType);

            var rerankerM3 = sfProv.Models.FirstOrDefault(m => m.ModelId == "BAAI/bge-reranker-v2-m3");
            Assert.NotNull(rerankerM3);
            Assert.Equal("rerank", rerankerM3.ModelType);

            // 2. 验证 TypeSafe AI System One 自动注册
            Assert.NotNull(config.SystemOne);
            Assert.True(config.SystemOne.Enabled);
            Assert.Equal("apikey_mock_typesafe_key", config.SystemOne.ApiKey);
            Assert.Equal("https://api.typesafe.ai/v1/systemone", config.SystemOne.BaseUrl);
            Assert.Equal("jev-latest", config.SystemOne.Model);

            // 3. 验证 ModelAssetResolver 全局 URN 解析
            var resolver = new ModelAssetResolver(configManager);
            var resolvedEmb = resolver.Resolve("provider:prov_sf:BAAI/bge-m3", "embedding");
            Assert.NotNull(resolvedEmb);
            Assert.True(resolvedEmb.IsReady);
            Assert.False(resolvedEmb.IsLocal);
            Assert.Equal(1024, resolvedEmb.Dimension);
            Assert.Equal("prov_sf", resolvedEmb.ProviderId);
            Assert.Equal("sk-mock-siliconflow-key", resolvedEmb.ApiKey);

            var resolvedRerank = resolver.Resolve("provider:prov_sf:BAAI/bge-reranker-v2-m3", "rerank");
            Assert.NotNull(resolvedRerank);
            Assert.True(resolvedRerank.IsReady);
            Assert.False(resolvedRerank.IsLocal);
            Assert.Equal("prov_sf", resolvedRerank.ProviderId);
            Assert.Equal("rerank", resolvedRerank.ModelType);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SILICONFLOW_API_KEY", origSfKey);
            Environment.SetEnvironmentVariable("SILICONFLOW_BASE_URL", origSfBase);
            Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", origTsKey);
        }
    }
}
