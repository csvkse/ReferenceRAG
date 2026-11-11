using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class SettingsControllerCognitiveBackendTests : IDisposable
{
    private readonly string _tempConfigFile;

    public SettingsControllerCognitiveBackendTests()
    {
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"rag_cog_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempConfigFile))
        {
            try { File.Delete(_tempConfigFile); } catch { }
        }
    }

    [Fact]
    public async Task SettingsController_TestCognitiveBackend_ReturnsValidResponse()
    {
        // Arrange
        var mockFactory = new Mock<ICognitiveEvidenceEvaluatorFactory>();
        var statAdapter = new StatisticalHeuristicEvidenceAdapter(0.50f);
        mockFactory
            .Setup(f => f.GetEvaluator(It.Is<CognitiveLexicalConfig>(c => c.Backend == CognitiveLexicalBackend.Statistical)))
            .Returns(statAdapter);

        var configManager = new ConfigManager(customConfigPath: _tempConfigFile);
        var controller = new SettingsController(
            configManager,
            new Mock<IModelManager>().Object,
            NullLogger<SettingsController>.Instance,
            cognitiveFactory: mockFactory.Object);

        // Act: Test Statistical Backend
        var statResult = await controller.TestCognitiveBackend(new TestCognitiveBackendRequest
        {
            Backend = CognitiveLexicalBackend.Statistical,
            AcceptanceThreshold = 0.50f
        }, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(statResult);
        dynamic val = okResult.Value!;
        Assert.True((bool)val.success);
        Assert.Equal("Statistical", (string)val.backend);
        Assert.True((bool)val.isAvailable);

        // Act: Test Disabled Backend
        var disabledResult = await controller.TestCognitiveBackend(new TestCognitiveBackendRequest
        {
            Backend = CognitiveLexicalBackend.Disabled
        }, CancellationToken.None);

        var okDisabled = Assert.IsType<OkObjectResult>(disabledResult);
        dynamic valDisabled = okDisabled.Value!;
        Assert.True((bool)valDisabled.success);
        Assert.Equal("Disabled", (string)valDisabled.backend);
    }
}
