using System;
using System.Linq;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

public class TokenizationCacheProxyTests
{
    [Fact]
    public void Tokenize_SanitizesBase64AndSvgNoise()
    {
        var rawText = "这是一篇文档 <svg width=\"100\"><path d=\"M0 0\"/></svg> 包含图片 data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg== 独孤九剑神功";
        var tokens = TokenizationCacheProxy.Default.Tokenize(rawText);

        Assert.Contains("独孤九剑", tokens);
        Assert.Contains("神功", tokens);
        Assert.DoesNotContain("ivborw0kggoaaaansuheugaaaaeaaaabca", tokens);
        Assert.DoesNotContain("svg", tokens);
    }

    [Fact]
    public void Tokenize_CachesRepeatedTokens()
    {
        var proxy = new TokenizationCacheProxy(maxEntries: 100);
        proxy.ClearCache();

        var text = "金庸武侠小说 雪山飞狐 飞狐外传";
        var tokens1 = proxy.Tokenize(text);
        var tokens2 = proxy.Tokenize(text);

        Assert.Equal(tokens1, tokens2);
        Assert.Equal(1, proxy.CacheCount);
    }

    [Fact]
    public void Tokenize_EvictsOldestWhenCapacityExceeded()
    {
        var proxy = new TokenizationCacheProxy(maxEntries: 500);
        proxy.ClearCache();

        for (int i = 0; i < 600; i++)
        {
            proxy.Tokenize($"独孤九剑第 {i} 式测试条目");
        }

        Assert.True(proxy.CacheCount <= 500);
    }

    [Fact]
    public void TokenizeForIndex_ProducesSpaceSeparatedKeywords()
    {
        var text = "张三丰 创立 武当派 太极拳";
        var indexed = TokenizationCacheProxy.Default.TokenizeForIndex(text);

        Assert.Contains("张三丰", indexed);
        Assert.Contains("武当派", indexed);
        Assert.Contains("太极拳", indexed);
    }
}
