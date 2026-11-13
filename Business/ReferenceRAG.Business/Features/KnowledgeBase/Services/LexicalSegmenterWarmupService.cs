using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.Search.Lexical;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 词法分词器预热宿主服务。
/// <para>
/// 把 jieba 词典（~1.2s，进程级静态单例）的加载从"第一个用户查询"提前到"进程启动后立刻"。
/// 用 <see cref="BackgroundService"/> 而非 <c>IHostedService.StartAsync</c>：宿主启动不被这 1.2s 拖慢，
/// 但用户第一次点检索时它早已就绪。
/// </para>
/// </summary>
public sealed class LexicalSegmenterWarmupService : BackgroundService
{
    private readonly ILogger<LexicalSegmenterWarmupService> _logger;

    public LexicalSegmenterWarmupService(ILogger<LexicalSegmenterWarmupService> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 让出启动线程，避免与 DI/路由构建抢 CPU
        return Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LexicalSegmenterWarmup.Warm();
            sw.Stop();
            _logger.LogInformation(
                "[LexicalSegmenterWarmup] 分词器预热完成，耗时 {Elapsed}ms（已从首查路径移出）",
                sw.ElapsedMilliseconds);
        }, stoppingToken);
    }
}
