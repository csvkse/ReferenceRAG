using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using ReferenceRAG.Business.Features.Search.Benchmark.Contracts;

namespace ReferenceRAG.Business.Features.Search.Benchmark.Adapters;

/// <summary>
/// CMRC 2018 中文通用机器阅读理解与百科问答数据集适配器
/// </summary>
public sealed class Cmrc2018DatasetAdapter : IGeneralBenchmarkDatasetAdapter
{
    private readonly string _filePath;

    public Cmrc2018DatasetAdapter(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string DatasetName => "CMRC-2018";

    public async IAsyncEnumerable<BenchmarkQuestionCase> StreamTestCasesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            yield break;
        }

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var contextId = item.TryGetProperty("context_id", out var cIdProp) ? cIdProp.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var contextText = item.TryGetProperty("context_text", out var ctxProp) ? ctxProp.GetString() ?? "" : "";

            if (!item.TryGetProperty("qas", out var qasProp) || qasProp.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var candidate = new BenchmarkContextCandidate(title, new[] { contextText });

            foreach (var qa in qasProp.EnumerateArray())
            {
                var qId = qa.TryGetProperty("query_id", out var qIdProp) ? qIdProp.GetString() ?? "" : "";
                var qText = qa.TryGetProperty("query_text", out var qTextProp) ? qTextProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(qText)) continue;

                string? goldAnswer = null;
                if (qa.TryGetProperty("answers", out var ansProp) && ansProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in ansProp.EnumerateArray())
                    {
                        var str = a.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            goldAnswer = str;
                            break;
                        }
                    }
                }

                yield return new BenchmarkQuestionCase(
                    QuestionId: qId,
                    Query: qText,
                    QuestionType: BenchmarkQuestionType.SingleHop,
                    GoldChunkIds: new[] { title, contextId },
                    SupportingEvidenceTitles: new[] { title },
                    GroundTruthAnswer: goldAnswer,
                    ContextCandidates: new[] { candidate }
                );
            }
        }
    }

    public async IAsyncEnumerable<BenchmarkCorpusDocument> StreamCorpusDocumentsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            yield break;
        }

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var contextId = item.TryGetProperty("context_id", out var cIdProp) ? cIdProp.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var contextText = item.TryGetProperty("context_text", out var ctxProp) ? ctxProp.GetString() ?? "" : "";

            if (!string.IsNullOrWhiteSpace(contextText))
            {
                yield return new BenchmarkCorpusDocument(
                    DocumentId: contextId,
                    Title: title,
                    Content: contextText,
                    Category: "CMRC_Encyclopedia"
                );
            }
        }
    }
}
