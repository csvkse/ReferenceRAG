using JiebaNet.Segmenter;

namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// jieba 分词器预热 —— 把"第一次总是慢"的最后一笔固定成本从请求路径挪到启动期。
/// <para>
/// <b>为什么需要：</b>jieba.NET 的词典（<c>WordDictionary</c>）与 HMM 模型（<c>Viterbi</c>）
/// 都是进程级静态单例，**首次调用 <c>Cut</c>/<c>CutForSearch</c> 时才加载**。
/// 生产 57,733 块的知识库实测这笔加载要 1.2s：修好"全库 OR"与重复行之后，
/// 冷启动首查 1,837ms 里有 1,210ms 落在 <c>kb.lexical.plan</c> 上，而真正的 FTS 匹配只有 157ms。
/// 也就是说，用户看到的"第一次慢"最后变成了"分词器第一次被唤醒"。
/// </para>
/// <para>
/// <b>做法：</b>启动后由后台宿主服务调用一次 <see cref="Warm"/>（幂等、异常吞掉、绝不阻塞启动）。
/// 由于词典是静态单例，预热一个实例即等价于预热全进程所有 <c>new JiebaSegmenter()</c> 使用点。
/// </para>
/// </summary>
public static class LexicalSegmenterWarmup
{
    private static int _warmed;

    /// <summary>是否已完成预热（用于健康检查/诊断展示）。</summary>
    public static bool IsWarmed => Volatile.Read(ref _warmed) == 1;

    /// <summary>
    /// 触发一次分词，迫使词典与 HMM 模型完成静态初始化。
    /// 幂等：重复调用直接返回；任何异常都不允许冒泡到宿主启动流程。
    /// </summary>
    public static void Warm()
    {
        if (Interlocked.Exchange(ref _warmed, 1) == 1) return;

        try
        {
            var segmenter = new JiebaSegmenter();

            // CutForSearch 走"精确模式 + 长词再切分"，是词法检索实际使用的入口；
            // 它同时会拉起 HMM（Viterbi）以处理未登录词，必须一并预热。
            _ = segmenter.CutForSearch("他笑嘻嘻的走上前去，拉着她的手");
            _ = segmenter.Cut("未登录词预热");
        }
        catch
        {
            // 预热是纯优化：失败只意味着首查仍需自行承担加载成本，不影响功能。
        }
    }
}
