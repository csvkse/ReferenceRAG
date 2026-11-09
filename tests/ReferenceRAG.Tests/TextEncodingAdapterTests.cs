using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ReferenceRAG.Infrastructure.Features.Storage.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class TextEncodingAdapterTests
{
    [Fact]
    public void DetectAndDecode_DecodesUtf8_WithAndWithoutBOM()
    {
        string expected = "风雪惊变，江南七怪与全真七子。";
        byte[] utf8WithoutBom = Encoding.UTF8.GetBytes(expected);
        byte[] utf8WithBom = Encoding.UTF8.GetPreamble();
        byte[] fullWithBom = new byte[utf8WithBom.Length + utf8WithoutBom.Length];
        Buffer.BlockCopy(utf8WithBom, 0, fullWithBom, 0, utf8WithBom.Length);
        Buffer.BlockCopy(utf8WithoutBom, 0, fullWithBom, utf8WithBom.Length, utf8WithoutBom.Length);

        string resultWithoutBom = TextEncodingAdapter.DetectAndDecode(utf8WithoutBom);
        string resultWithBom = TextEncodingAdapter.DetectAndDecode(fullWithBom);

        Assert.Equal(expected, resultWithoutBom);
        Assert.Equal(expected, resultWithBom);
    }

    [Fact]
    public void DetectAndDecode_DecodesGB18030_WithoutGarbledChars()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string expected = "第一回　古道腾驹惊白马。降龙十八掌威震江湖！";
        var gbk = Encoding.GetEncoding("GB18030");
        byte[] gbkBytes = gbk.GetBytes(expected);

        // 如果按 UTF-8 解码必然会出现 \uFFFD
        string brokenUtf8 = Encoding.UTF8.GetString(gbkBytes);
        Assert.Contains('\uFFFD', brokenUtf8);

        // 使用 TextEncodingAdapter 自动适配探测
        string decoded = TextEncodingAdapter.DetectAndDecode(gbkBytes);
        Assert.Equal(expected, decoded);
        Assert.DoesNotContain('\uFFFD', decoded);
    }

    [Fact]
    public async Task SafeReadFileAsync_ReadsActualJinYongFile_Cleanly()
    {
        // 查找 env/data 目录下的越女剑
        string? projectRoot = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")))
            {
                projectRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(projectRoot);
        var files = Directory.GetFiles(Path.Combine(projectRoot, "env", "data"), "*越女剑*.TXT", SearchOption.AllDirectories);
        if (files.Length == 0) return; // CI 或无数据环境跳过

        string filePath = files[0];
        string? content = await TextEncodingAdapter.SafeReadFileAsync(filePath);

        Assert.NotNull(content);
        Assert.Contains("越女剑", content);
        Assert.Contains("青衣剑士", content);
        Assert.DoesNotContain('\uFFFD', content);
    }
}
