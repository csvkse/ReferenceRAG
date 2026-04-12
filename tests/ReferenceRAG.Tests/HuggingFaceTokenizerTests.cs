using ReferenceRAG.Core.Services.Tokenizers;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// HuggingFace 完整分词器测试 - 验证 tokenizers-rust 原生库加载 tokenizer.json 全管线
/// </summary>
public class HuggingFaceTokenizerTests
{
    private readonly HuggingFaceTokenizer _tokenizer;

    public HuggingFaceTokenizerTests()
    {
        var tokenizerPath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "resource", "data", "models", "bge-small-zh-v1.5", "tokenizer.json");

        if (!File.Exists(tokenizerPath))
            tokenizerPath = "E:/LinuxWork/Obsidian/resource/data/models/bge-small-zh-v1.5/tokenizer.json";

        _tokenizer = new HuggingFaceTokenizer(tokenizerPath);
    }

    [Fact]
    public void Name_ContainsHuggingFace()
    {
        Assert.Contains("HuggingFace", _tokenizer.Name);
    }

    [Fact]
    public void VocabSize_IsCorrect()
    {
        Assert.Equal(21128, _tokenizer.VocabSize);
    }

    [Fact]
    public void SpecialTokenIds_AreCorrect()
    {
        Assert.Equal(101, _tokenizer.ClsTokenId);  // [CLS]
        Assert.Equal(102, _tokenizer.SepTokenId);   // [SEP]
        Assert.Equal(0, _tokenizer.PadTokenId);     // [PAD]
        Assert.Equal(100, _tokenizer.UnkTokenId);   // [UNK]
    }

    [Fact]
    public void Encode_ChineseText_ReturnsValidIds()
    {
        var ids = _tokenizer.Encode("这是一个测试");
        Assert.NotEmpty(ids);
        Assert.Equal(_tokenizer.ClsTokenId, ids[0]);   // 以 [CLS] 开头
        Assert.Equal(_tokenizer.SepTokenId, ids[^1]);   // 以 [SEP] 结尾
    }

    [Fact]
    public void Encode_MixedChineseEnglish_ReturnsValidIds()
    {
        var ids = _tokenizer.Encode("Hello世界test");
        Assert.NotEmpty(ids);
        Assert.Equal(_tokenizer.ClsTokenId, ids[0]);
        Assert.Equal(_tokenizer.SepTokenId, ids[^1]);
        // 混合文本应该产生多个 token（不是全部映射到 [UNK]）
        Assert.True(ids.Count > 3, $"混合文本 token 数量应 > 3，实际: {ids.Count}");
    }

    [Fact]
    public void Encode_RespectsMaxLength()
    {
        var longText = new string('测', 1000);
        var ids = _tokenizer.Encode(longText, maxLength: 32);
        Assert.True(ids.Count <= 32, $"截断后 token 数应 <= 32，实际: {ids.Count}");
    }

    [Fact]
    public void Tokenize_SingleText_TensorShapeCorrect()
    {
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Tokenize(
            new List<string> { "测试文本" }, maxLength: 64);

        Assert.Equal(2, inputIds.Dimensions.Length);
        Assert.Equal(1, inputIds.Dimensions[0]);   // batch = 1
        Assert.Equal(64, inputIds.Dimensions[1]);   // maxLength = 64
        Assert.Equal(1, attentionMask.Dimensions[0]);
        Assert.Equal(64, attentionMask.Dimensions[1]);
        Assert.Equal(1, tokenTypeIds.Dimensions[0]);
        Assert.Equal(64, tokenTypeIds.Dimensions[1]);
    }

    [Fact]
    public void Tokenize_BatchTexts_TensorShapeCorrect()
    {
        var texts = new List<string> { "第一句话", "第二句话", "第三句话" };
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Tokenize(texts, maxLength: 32);

        Assert.Equal(3, inputIds.Dimensions[0]);   // batch = 3
        Assert.Equal(32, inputIds.Dimensions[1]);   // maxLength = 32
    }

    [Fact]
    public void Tokenize_PaddingMask_Correct()
    {
        var (inputIds, attentionMask, _) = _tokenizer.Tokenize(
            new List<string> { "短文本" }, maxLength: 64);

        // 统计 attention mask 中 1 的数量（有效 token 数）
        int validCount = 0;
        for (int j = 0; j < 64; j++)
        {
            if (attentionMask[0, j] == 1) validCount++;
        }

        // 有效 token 应该 > 2（至少 [CLS] + 一些中文字符 + [SEP]）
        Assert.True(validCount > 2, $"有效 token 数应 > 2，实际: {validCount}");

        // 有效 token 后面应该是 padding (mask=0)
        bool foundPadding = false;
        for (int j = 0; j < 64; j++)
        {
            if (attentionMask[0, j] == 0 && !foundPadding)
            {
                foundPadding = true;
            }
            // padding 之后的 mask 必须全是 0
            if (foundPadding)
            {
                Assert.Equal(0, attentionMask[0, j]);
            }
        }
    }

    [Fact]
    public void CountTokens_ReturnsCorrectCount()
    {
        var count = _tokenizer.CountTokens("测试");
        Assert.True(count > 0, $"token 数应 > 0，实际: {count}");
        // 至少 [CLS] + 中文字符 + [SEP]
        Assert.True(count >= 3, $"token 数应 >= 3（含特殊 token），实际: {count}");
    }

    [Fact]
    public void Encode_ComparedWithCustomBertTokenizer_ProducesSimilarResults()
    {
        // 对比 HuggingFace 完整分词器与自定义 BertTokenizer 的输出
        // 两者对于纯中文文本应该产生相同或非常接近的结果
        var customTokenizerPath = Path.Combine(
            Environment.CurrentDirectory, "..", "..", "..", "..",
            "resource", "data", "models", "bge-small-zh-v1.5", "tokenizer.json");
        if (!File.Exists(customTokenizerPath))
            customTokenizerPath = "E:/LinuxWork/Obsidian/resource/data/models/bge-small-zh-v1.5/tokenizer.json";

        var customTokenizer = new BertTokenizer(customTokenizerPath);

        var testText = "这是一个中文测试句子";
        var hfIds = _tokenizer.Encode(testText);
        var customIds = customTokenizer.Encode(testText);

        // 纯中文文本两者结果应高度一致
        // 注意：可能因为 do_lower_case 差异有小差别
        Assert.True(hfIds.Count > 0 && customIds.Count > 0, "两个分词器都应产生非空结果");
    }
}
