using Microsoft.ML.OnnxRuntime.Tensors;
using ReferenceRAG.Core.Services.Tokenizers;
using Xunit;
using System.Text.Json;
using static Xunit.Skip;

namespace ReferenceRAG.Tests;

/// <summary>
/// 对比 BertTokenizer.TokenizeSingle（tensor 路径）与 HuggingFace 参考输出
/// 这是服务实际使用的代码路径
/// </summary>
public class TokenizeSingleAlignmentTests
{
    private static readonly string? TokenizerPath = ResolveTokenizerPath();
    private static readonly List<TokenizeSingleCase> ReferenceCases = LoadReferenceCases();

    private static string? ResolveTokenizerPath()
    {
        var paths = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..",
                "resource", "data", "models", "bge-small-zh-v1.5", "tokenizer.json"),
            "E:/LinuxWork/Obsidian/resource/data/models/bge-small-zh-v1.5/tokenizer.json"
        };

        return paths.FirstOrDefault(File.Exists);
    }

    private static List<TokenizeSingleCase> LoadReferenceCases()
    {
        var referencePath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "tests", "tokenize_single_reference.json");
        if (!File.Exists(referencePath))
            referencePath = "E:/LinuxWork/Obsidian/tests/tokenize_single_reference.json";

        if (File.Exists(referencePath))
        {
            var json = File.ReadAllText(referencePath);
            return JsonSerializer.Deserialize<List<TokenizeSingleCase>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }

        return new();
    }

    [SkippableFact]
    public void TokenizeSingle_MatchesHuggingFace()
    {
        Skip.If(TokenizerPath == null, "Tokenizer 文件不存在，跳过测试");
        Skip.If(ReferenceCases.Count == 0, "参考用例文件不存在，跳过测试");

        var tokenizer = new BertTokenizer(TokenizerPath!);

        var mismatches = new List<string>();
        int matched = 0;

        foreach (var tc in ReferenceCases)
        {
            // 调用 Tokenize 方法（内部使用 TokenizeSingle）
            var result = tokenizer.Tokenize(new List<string> { tc.Text }, 512);
            var inputIds = result.InputIds;

            // 提取第一个 batch 的 IDs
            var actualIds = new List<int>();
            for (int j = 0; j < 512; j++)
            {
                var id = (int)inputIds[0, j];
                // 遇到 padding 后停止（attention_mask = 0 的位置）
                if (result.AttentionMask[0, j] == 0) break;
                actualIds.Add(id);
            }

            var expectedIds = tc.FullIds;

            if (actualIds.SequenceEqual(expectedIds))
            {
                matched++;
            }
            else
            {
                var textPreview = tc.Text.Length > 40 ? tc.Text[..40] + "..." : tc.Text;
                int firstDiff = -1;
                for (int i = 0; i < Math.Max(expectedIds.Count, actualIds.Count); i++)
                {
                    var e = i < expectedIds.Count ? expectedIds[i] : -1;
                    var a = i < actualIds.Count ? actualIds[i] : -1;
                    if (e != a) { firstDiff = i; break; }
                }
                mismatches.Add(
                    $"\"{textPreview}\": " +
                    $"first_diff@{firstDiff}, " +
                    $"expected({expectedIds.Count})=[{string.Join(",", expectedIds.Take(20))}], " +
                    $"actual({actualIds.Count})=[{string.Join(",", actualIds.Take(20))}]");
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail($"Matched {matched}/{ReferenceCases.Count}, {mismatches.Count} mismatched:\n" +
                         string.Join("\n", mismatches));
        }
    }
}

internal class TokenizeSingleCase
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("full_ids")]
    public List<int> FullIds { get; set; } = new();
}
