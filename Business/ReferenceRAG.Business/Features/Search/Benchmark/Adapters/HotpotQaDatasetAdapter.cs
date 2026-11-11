using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using ReferenceRAG.Business.Features.Search.Benchmark.Contracts;

namespace ReferenceRAG.Business.Features.Search.Benchmark.Adapters;

/// <summary>
/// HotpotQA 多跳基准评测数据集适配器
/// </summary>
public sealed class HotpotQaDatasetAdapter : IGeneralBenchmarkDatasetAdapter
{
    private static readonly Regex PassageHeaderRegex = new(@"Passage\s+\d+:\s*\r?\n([^\r\n]+)", RegexOptions.Compiled);
    private readonly string _filePath;

    public HotpotQaDatasetAdapter(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string DatasetName => "HotpotQA";

    public async IAsyncEnumerable<BenchmarkQuestionCase> StreamTestCasesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            yield break;
        }

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream);

        string? line;
        int caseIndex = 0;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var input = root.TryGetProperty("input", out var inputProp) ? inputProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(input)) continue;

            // 提取答案
            string? answer = null;
            if (root.TryGetProperty("answers", out var answersProp) && answersProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var ans in answersProp.EnumerateArray())
                {
                    answer = ans.GetString();
                    if (!string.IsNullOrWhiteSpace(answer)) break;
                }
            }

            // 解析 context 中的候选 passages
            var contextStr = root.TryGetProperty("context", out var ctxProp) ? ctxProp.GetString() : "";
            var contextCandidates = new List<BenchmarkContextCandidate>();
            var supportingTitles = new List<string>();

            if (!string.IsNullOrWhiteSpace(contextStr))
            {
                // 按 "Passage N:" 分割
                var passages = contextStr.Split(new[] { "\nPassage " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in passages)
                {
                    var cleanP = p.StartsWith("Passage ") ? p.Substring(8) : p;
                    var colonIdx = cleanP.IndexOf(':');
                    if (colonIdx > 0 && colonIdx < 10)
                    {
                        cleanP = cleanP.Substring(colonIdx + 1).TrimStart();
                    }

                    var lines = cleanP.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var title = lines[0].Trim();
                        var bodyLines = new List<string>();
                        for (int i = 1; i < lines.Length; i++)
                        {
                            var bl = lines[i].Trim();
                            if (!string.IsNullOrWhiteSpace(bl)) bodyLines.Add(bl);
                        }

                        contextCandidates.Add(new BenchmarkContextCandidate(title, bodyLines));
                        // 启发式：如果标题中的词项在输入问句中出现，作为潜在支撑事实
                        if (input.Contains(title, StringComparison.OrdinalIgnoreCase))
                        {
                            supportingTitles.Add(title);
                        }
                    }
                }
            }

            caseIndex++;
            string? qId = (root.TryGetProperty("id", out var idProp) || root.TryGetProperty("_id", out idProp))
                ? idProp.GetString()
                : null;
            var questionId = !string.IsNullOrWhiteSpace(qId) ? qId : $"hotpotqa_{caseIndex:D5}";

            var qType = input.Contains(" or ", StringComparison.OrdinalIgnoreCase) || input.Contains("both", StringComparison.OrdinalIgnoreCase)
                ? BenchmarkQuestionType.MultiHopComparison
                : BenchmarkQuestionType.MultiHopBridge;

            yield return new BenchmarkQuestionCase(
                QuestionId: questionId,
                Query: input,
                QuestionType: qType,
                GoldChunkIds: supportingTitles,
                SupportingEvidenceTitles: supportingTitles,
                GroundTruthAnswer: answer,
                ContextCandidates: contextCandidates
            );
        }
    }

    public async IAsyncEnumerable<BenchmarkCorpusDocument> StreamCorpusDocumentsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var tc in StreamTestCasesAsync(ct))
        {
            if (tc.ContextCandidates != null)
            {
                foreach (var c in tc.ContextCandidates)
                {
                    if (seenTitles.Add(c.Title))
                    {
                        yield return new BenchmarkCorpusDocument(
                            DocumentId: $"doc_{Guid.NewGuid():N}",
                            Title: c.Title,
                            Content: c.FullText,
                            Category: "HotpotQA_Corpus"
                        );
                    }
                }
            }
        }
    }
}
