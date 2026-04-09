using Microsoft.ML.OnnxRuntime.Tensors;
using ObsidianRAG.Core.Services.Tokenizers;
using Xunit;
using System.Text.Json;

namespace ObsidianRAG.Tests;

/// <summary>
/// 对比 BertTokenizer.TokenizeSingle（tensor 路径）与 HuggingFace 参考输出
/// 这是服务实际使用的代码路径
/// </summary>
public class TokenizeSingleAlignmentTests
{
    private readonly BertTokenizer _tokenizer;
    private readonly List<TokenizeSingleCase> _referenceCases;

    public TokenizeSingleAlignmentTests()
    {
        var tokenizerPath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "resource", "models", "bge-small-zh-v1.5", "tokenizer.json");
        if (!File.Exists(tokenizerPath))
            tokenizerPath = "E:/LinuxWork/Obsidian/resource/models/bge-small-zh-v1.5/tokenizer.json";
        _tokenizer = new BertTokenizer(tokenizerPath);

        var referencePath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "tests", "tokenize_single_reference.json");
        if (!File.Exists(referencePath))
            referencePath = "E:/LinuxWork/Obsidian/tests/tokenize_single_reference.json";

        if (File.Exists(referencePath))
        {
            var json = File.ReadAllText(referencePath);
            _referenceCases = JsonSerializer.Deserialize<List<TokenizeSingleCase>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        else
        {
            _referenceCases = new();
        }
    }

    [Fact]
    public void TokenizeSingle_MatchesHuggingFace()
    {
        if (_referenceCases.Count == 0) return;

        var mismatches = new List<string>();
        int matched = 0;

        foreach (var tc in _referenceCases)
        {
            // 调用 Tokenize 方法（内部使用 TokenizeSingle）
            var result = _tokenizer.Tokenize(new List<string> { tc.Text }, 512);
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
            Assert.Fail($"Matched {matched}/{_referenceCases.Count}, {mismatches.Count} mismatched:\n" +
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
