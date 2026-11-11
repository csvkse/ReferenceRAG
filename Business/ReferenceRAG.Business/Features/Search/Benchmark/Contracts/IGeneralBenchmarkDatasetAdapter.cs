using System.Collections.Generic;
using System.Threading;

namespace ReferenceRAG.Business.Features.Search.Benchmark.Contracts;

/// <summary>
/// 通用知识库评测数据集适配器契约 (Adapter Pattern)
/// </summary>
public interface IGeneralBenchmarkDatasetAdapter
{
    /// <summary>
    /// 数据集唯一名称 (如 HotpotQA, MuSiQue, CMRC-2018)
    /// </summary>
    string DatasetName { get; }

    /// <summary>
    /// 流式加载评测用例集合
    /// </summary>
    IAsyncEnumerable<BenchmarkQuestionCase> StreamTestCasesAsync(CancellationToken ct = default);

    /// <summary>
    /// 流式加载附属语料文档 (若该数据集提供原生全量语料)
    /// </summary>
    IAsyncEnumerable<BenchmarkCorpusDocument> StreamCorpusDocumentsAsync(CancellationToken ct = default);
}
