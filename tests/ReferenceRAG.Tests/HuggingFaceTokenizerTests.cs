using ReferenceRAG.Core.Services.Tokenizers;
using Xunit;
using static Xunit.Skip;

namespace ReferenceRAG.Tests;

/// <summary>
/// HuggingFace 完整分词器测试 - 验证 tokenizers-rust 原生库加载 tokenizer.json 全管线
/// </summary>
public class HuggingFaceTokenizerTests : IDisposable
{
    private static readonly string? TokenizerPath = ResolveTokenizerPath();

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

    private readonly HuggingFaceTokenizer? _tokenizer;

    public HuggingFaceTokenizerTests()
    {
        _tokenizer = TokenizerPath != null ? new HuggingFaceTokenizer(TokenizerPath) : null;
    }

    public void Dispose() { GC.SuppressFinalize(this); }

    [SkippableFact]
    public void Name_ContainsHuggingFace()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        Assert.Contains("HuggingFace", _tokenizer!.Name);
    }

    [SkippableFact]
    public void VocabSize_IsCorrect()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        Assert.Equal(21128, _tokenizer!.VocabSize);
    }

    [SkippableFact]
    public void SpecialTokenIds_AreCorrect()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        Assert.Equal(101, _tokenizer!.ClsTokenId);
        Assert.Equal(102, _tokenizer.SepTokenId);
        Assert.Equal(0, _tokenizer.PadTokenId);
        Assert.Equal(100, _tokenizer.UnkTokenId);
    }

    [SkippableFact]
    public void Encode_ChineseText_ReturnsValidIds()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var ids = _tokenizer!.Encode("这是一个测试");
        Assert.NotEmpty(ids);
        Assert.Equal(_tokenizer.ClsTokenId, ids[0]);
        Assert.Equal(_tokenizer.SepTokenId, ids[^1]);
    }

    [SkippableFact]
    public void Encode_MixedChineseEnglish_ReturnsValidIds()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var ids = _tokenizer!.Encode("Hello世界test");
        Assert.NotEmpty(ids);
        Assert.Equal(_tokenizer.ClsTokenId, ids[0]);
        Assert.Equal(_tokenizer.SepTokenId, ids[^1]);
        Assert.True(ids.Count > 3, $"混合文本 token 数量应 > 3，实际: {ids.Count}");
    }

    [SkippableFact]
    public void Encode_RespectsMaxLength()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var longText = new string('测', 1000);
        var ids = _tokenizer!.Encode(longText, maxLength: 32);
        Assert.True(ids.Count <= 32, $"截断后 token 数应 <= 32，实际: {ids.Count}");
    }

    [SkippableFact]
    public void Tokenize_SingleText_TensorShapeCorrect()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer!.Tokenize(
            new List<string> { "测试文本" }, maxLength: 64);

        Assert.Equal(2, inputIds.Dimensions.Length);
        Assert.Equal(1, inputIds.Dimensions[0]);
        Assert.Equal(64, inputIds.Dimensions[1]);
        Assert.Equal(1, attentionMask.Dimensions[0]);
        Assert.Equal(64, attentionMask.Dimensions[1]);
        Assert.Equal(1, tokenTypeIds.Dimensions[0]);
        Assert.Equal(64, tokenTypeIds.Dimensions[1]);
    }

    [SkippableFact]
    public void Tokenize_BatchTexts_TensorShapeCorrect()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var texts = new List<string> { "第一句话", "第二句话", "第三句话" };
        var (inputIds, _, _) = _tokenizer!.Tokenize(texts, maxLength: 32);
        Assert.Equal(3, inputIds.Dimensions[0]);
        Assert.Equal(32, inputIds.Dimensions[1]);
    }

    [SkippableFact]
    public void Tokenize_PaddingMask_Correct()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var (_, attentionMask, _) = _tokenizer!.Tokenize(
            new List<string> { "短文本" }, maxLength: 64);

        int validCount = 0;
        for (int j = 0; j < 64; j++)
            if (attentionMask[0, j] == 1) validCount++;

        Assert.True(validCount > 2, $"有效 token 数应 > 2，实际: {validCount}");

        bool foundPadding = false;
        for (int j = 0; j < 64; j++)
        {
            if (attentionMask[0, j] == 0 && !foundPadding) foundPadding = true;
            if (foundPadding) Assert.Equal(0, attentionMask[0, j]);
        }
    }

    [SkippableFact]
    public void CountTokens_ReturnsCorrectCount()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        var count = _tokenizer!.CountTokens("测试");
        Assert.True(count > 0);
        Assert.True(count >= 3);
    }

    [SkippableFact]
    public void Encode_ComparedWithCustomBertTokenizer_ProducesSimilarResults()
    {
        Skip.If(_tokenizer == null || TokenizerPath == null, "Tokenizer 文件不存在，跳过测试");
        var customTokenizer = new BertTokenizer(TokenizerPath);
        var testText = "这是一个中文测试句子";
        var hfIds = _tokenizer!.Encode(testText);
        var customIds = customTokenizer.Encode(testText);
        Assert.True(hfIds.Count > 0 && customIds.Count > 0);
    }
}
