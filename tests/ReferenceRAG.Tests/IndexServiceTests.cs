using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Tests;

public class IndexServiceTests
{
    private sealed class RecordingEventPublisher : IIndexEventPublisher
    {
        private readonly TaskCompletionSource<IndexCompletedEvent> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IndexCompletedEvent> IndexCompleted => _completed;

        public Task PublishAsync(string name, object payload, CancellationToken cancellationToken = default)
        {
            if (name == "IndexCompleted" && payload is IndexCompletedEvent completed)
                _completed.TrySetResult(completed);
            return Task.CompletedTask;
        }
    }

    private static IndexService CreateService(
        RecordingEventPublisher publisher,
        Mock<IVectorStore> vectorStore)
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });

        var services = new ServiceCollection();
        services.AddSingleton(vectorStore.Object);
        services.AddSingleton<IEmbeddingService>(new Mock<IEmbeddingService>().Object);
        var provider = services.BuildServiceProvider();

        return new IndexService(
            provider,
            publisher,
            new ConfigManager(),
            new Mock<IFileIndexPipeline>().Object,
            new Mock<IFileProcessingGuard>().Object,
            NullLogger<IndexService>.Instance);
    }

    [Fact]
    public async Task StartIndexAsync_PublishesIndexCompleted_WhenJobFails()
    {
        var publisher = new RecordingEventPublisher();
        var vectorStore = new Mock<IVectorStore>();
        vectorStore
            .Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vector store unavailable"));

        var service = CreateService(publisher, vectorStore);

        var job = await service.StartIndexAsync(new IndexRequest());

        await Task.WhenAny(publisher.IndexCompleted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(
            publisher.IndexCompleted.Task.IsCompleted,
            $"任务 {job.Id} 失败后应在 15 秒内广播 IndexCompleted 事件");
        var payload = await publisher.IndexCompleted.Task;
        Assert.Equal(job.Id, payload.IndexId);
        Assert.Equal(IndexStatus.Failed, job.Status);
    }
}
