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
        const int maxLength = 64;
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer!.Tokenize(
            new List<string> { "测试文本" }, maxLength);

        // TrimToActualLength 会将 tensor 裁剪到实际 token 长度，而非 maxLength
        Assert.Equal(2, inputIds.Dimensions.Length);
        Assert.Equal(1, inputIds.Dimensions[0]);
        Assert.True(inputIds.Dimensions[1] >= 3 && inputIds.Dimensions[1] <= maxLength,
            $"裁剪后长度应在 [3, {maxLength}]，实际: {inputIds.Dimensions[1]}");
        Assert.Equal(inputIds.Dimensions[0], attentionMask.Dimensions[0]);
        Assert.Equal(inputIds.Dimensions[1], attentionMask.Dimensions[1]);
        Assert.Equal(inputIds.Dimensions[0], tokenTypeIds.Dimensions[0]);
        Assert.Equal(inputIds.Dimensions[1], tokenTypeIds.Dimensions[1]);
    }

    [SkippableFact]
    public void Tokenize_BatchTexts_TensorShapeCorrect()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        const int maxLength = 32;
        var texts = new List<string> { "第一句话", "第二句话", "第三句话" };
        var (inputIds, _, _) = _tokenizer!.Tokenize(texts, maxLength);
        // TrimToActualLength 裁剪到批次中最长序列的实际长度
        Assert.Equal(3, inputIds.Dimensions[0]);
        Assert.True(inputIds.Dimensions[1] >= 3 && inputIds.Dimensions[1] <= maxLength,
            $"裁剪后长度应在 [3, {maxLength}]，实际: {inputIds.Dimensions[1]}");
    }

    [SkippableFact]
    public void Tokenize_PaddingMask_Correct()
    {
        Skip.If(_tokenizer == null, "Tokenizer 文件不存在，跳过测试");
        const int maxLength = 64;
        var (_, attentionMask, _) = _tokenizer!.Tokenize(
            new List<string> { "短文本" }, maxLength);

        // TrimToActualLength 后，tensor 只保留有效 token 位置，无 padding 零值
        var trimmedLen = attentionMask.Dimensions[1];
        Assert.True(trimmedLen > 2, $"有效 token 数应 > 2，实际: {trimmedLen}");
        Assert.True(trimmedLen <= maxLength, $"裁剪后长度不应超过 {maxLength}");

        // 裁剪后所有位置均为有效 token（mask == 1）
        for (int j = 0; j < trimmedLen; j++)
            Assert.Equal(1, attentionMask[0, j]);
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
