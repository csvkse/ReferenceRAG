using ObsidianRAG.Core.Services.Tokenizers;
using Xunit;
using System.Text.Json;

namespace ObsidianRAG.Tests;

/// <summary>
/// 对比自定义 BertTokenizer 与 HuggingFace Python 参考输出的差异
/// 参考数据使用 do_lower_case=True 生成，与 LM Studio 行为对齐
/// </summary>
public class TokenizerAlignmentTests
{
    private readonly BertTokenizer _tokenizer;
    private readonly List<TokenizerTestCase> _referenceCases;

    public TokenizerAlignmentTests()
    {
        var tokenizerPath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "resource", "data", "models", "bge-small-zh-v1.5", "tokenizer.json");

        if (!File.Exists(tokenizerPath))
            tokenizerPath = "E:/LinuxWork/Obsidian/resource/data/models/bge-small-zh-v1.5/tokenizer.json";

        _tokenizer = new BertTokenizer(tokenizerPath);

        var referencePath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "tests", "tokenizer_reference.json");

        if (!File.Exists(referencePath))
            referencePath = "E:/LinuxWork/Obsidian/tests/tokenizer_reference.json";

        if (File.Exists(referencePath))
        {
            var json = File.ReadAllText(referencePath);
            _referenceCases = JsonSerializer.Deserialize<List<TokenizerTestCase>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        else
        {
            _referenceCases = new();
        }
    }

    [Fact]
    public void AllReferenceCases_ReportMismatches()
    {
        if (_referenceCases.Count == 0) return;

        var mismatches = new List<string>();
        int matched = 0;

        foreach (var tc in _referenceCases)
        {
            var actualIds = _tokenizer.Encode(tc.Text);
            // 去掉 [CLS](101) 和 [SEP](102)
            var actualContent = actualIds.Skip(1).Take(actualIds.Count - 2).ToList();
            var expectedContent = tc.TokenIds;

            if (actualContent.SequenceEqual(expectedContent))
            {
                matched++;
            }
            else
            {
                var textPreview = tc.Text.Length > 30 ? tc.Text[..30] + "..." : tc.Text;
                // 找出第一个不同的位置
                int firstDiff = -1;
                for (int i = 0; i < Math.Max(expectedContent.Count, actualContent.Count); i++)
                {
                    var e = i < expectedContent.Count ? expectedContent[i] : -1;
                    var a = i < actualContent.Count ? actualContent[i] : -1;
                    if (e != a) { firstDiff = i; break; }
                }
                mismatches.Add(
                    $"\"{textPreview}\": " +
                    $"first_diff@{firstDiff}, " +
                    $"expected({expectedContent.Count})=[{string.Join(",", expectedContent.Take(15))}], " +
                    $"actual({actualContent.Count})=[{string.Join(",", actualContent.Take(15))}]");
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail($"Matched {matched}/{_referenceCases.Count}, {mismatches.Count} mismatched:\n" +
                         string.Join("\n", mismatches));
        }
    }
}

internal class TokenizerTestCase
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("token_ids")]
    public List<int> TokenIds { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("tokens")]
    public List<string> Tokens { get; set; } = new();
}
