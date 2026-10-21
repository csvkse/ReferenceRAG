using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：前端所有视图与布局完整性守卫
/// 防止再次出现“__returned__ 暴露未声明变量导致浏览器端 ReferenceError 静默白屏/空白页”故障。
/// </summary>
public sealed class FrontendViewIntegrityTests
{
    private static readonly HashSet<string> JsGlobals = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "undefined", "NaN", "Infinity",
        "Math", "Date", "Array", "Object", "String", "Number", "Boolean", "RegExp", "Error", "JSON", "console",
        "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURI", "encodeURIComponent", "decodeURI", "decodeURIComponent",
        "$event", "$slots", "$attrs", "$emit", "$refs", "$parent", "$root", "window", "document",
        "if", "else", "typeof", "instanceof", "void", "delete", "new", "in", "of", "return",
        "case", "break", "continue", "default", "throw", "catch", "finally", "try", "switch",
        "while", "do", "for", "const", "let", "var", "function", "async", "await", "yield", "this"
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到代码仓根目录 (ReferenceRAG.slnx)");
    }

    private static List<string> GetFrontendViewFiles(string root)
    {
        var dirs = new[]
        {
            Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "app", "views"),
            Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "app", "layouts")
        };

        var files = new List<string>();
        foreach (var d in dirs)
        {
            if (Directory.Exists(d))
            {
                files.AddRange(Directory.GetFiles(d, "*.js", SearchOption.AllDirectories));
            }
        }
        return files;
    }

    [Fact]
    public void AllFrontendViews_MustNotExportUndeclaredIdentifiers_InReturnedScope()
    {
        var root = FindRepoRoot();
        var files = GetFrontendViewFiles(root);
        Assert.NotEmpty(files);

        var errors = new List<string>();

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var returnedMatch = Regex.Match(content, @"const\s+__returned__\s*=\s*\{([\s\S]*?)\};?\s*return\s+__returned__;");
            if (!returnedMatch.Success) continue;

            var returnedBody = returnedMatch.Groups[1].Value;
            var beforeSource = content.Substring(0, returnedMatch.Index);

            // 提取 setup 形参
            var setupParamMatch = Regex.Match(content, @"setup\s*\(([^)]*)\)");
            var setupParams = new HashSet<string>(StringComparer.Ordinal);
            if (setupParamMatch.Success)
            {
                foreach (Match pm in Regex.Matches(setupParamMatch.Groups[1].Value, @"\b([a-zA-Z_$][a-zA-Z0-9_$]*)\b"))
                {
                    setupParams.Add(pm.Groups[1].Value);
                }
            }

            // 提取所有导出的属性名（支持简写 property、冒号赋值、getter）
            var exportedKeys = new List<string>();
            foreach (Match m in Regex.Matches(returnedBody, @"(?:get\s+([a-zA-Z0-9_$]+)\s*\(\)|([a-zA-Z0-9_$]+)(?:\s*:|\s*,|\s*$))"))
            {
                var key = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(key) && key != "get" && key != "set")
                {
                    exportedKeys.Add(key);
                }
            }

            // 逐一校验词法作用域内是否存在声明
            foreach (var key in exportedKeys)
            {
                if (JsGlobals.Contains(key) || setupParams.Contains(key)) continue;

                // 校验在 __returned__ 声明之前是否有 const/let/var/function/class/import 声明该标识符
                var declPattern = $@"(?:const|let|var)\s+[^;=]*?\b{Regex.Escape(key)}\b|(?:function|class)\s+{Regex.Escape(key)}\b|import\s+[^;]*?\b{Regex.Escape(key)}\b";
                if (!Regex.IsMatch(beforeSource, declPattern))
                {
                    var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                    errors.Add($"[{relPath}] 暴露的变量 '{key}' 在 setup 作用域及外层模块未找到声明，运行时将抛出 ReferenceError 导致白屏！");
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"发现 {errors.Count} 处前端未声明变量导出导致潜在白屏的严重缺陷:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void PerformanceView_ChatEvalDeclarations_MustBeCompleteAndMatchReturnedScope()
    {
        var root = FindRepoRoot();
        var perfPath = Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "app", "views", "Performance.js");
        Assert.True(File.Exists(perfPath), $"Performance.js 未找到: {perfPath}");

        var content = File.ReadAllText(perfPath);

        // 核心防回归校验：确保 11 个评测核心变量与方法存在明确声明
        var requiredIdentifiers = new[]
        {
            "chatEvalCases",
            "chatEvalRunning",
            "chatEvalProgress",
            "chatEvalStats",
            "chatEvalColumns",
            "chatEvalDimensionColumns",
            "runSingleChatEvalCase",
            "runAllChatEvalCases",
            "cancelChatEval",
            "exportChatEvalReport",
            "exportChatEvalCasesJson"
        };

        foreach (var id in requiredIdentifiers)
        {
            var hasDecl = Regex.IsMatch(content, $@"(?:const|let|var|function)\s+{Regex.Escape(id)}\b");
            Assert.True(hasDecl, $"Performance.js 缺失核心评测标识符声明: {id}");
        }
    }

    [Fact]
    public void NodeCheckMjs_QualityGate_MustPassWithoutErrors()
    {
        var root = FindRepoRoot();
        var checkScript = Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "check.mjs");
        Assert.True(File.Exists(checkScript), $"check.mjs 未找到: {checkScript}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "check.mjs",
            WorkingDirectory = Path.GetDirectoryName(checkScript),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(startInfo);
            Assert.NotNull(proc);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            Assert.True(proc.ExitCode == 0, $"node check.mjs 门禁未通过 (退出码 {proc.ExitCode}):\nSTDOUT: {stdout}\nSTDERR: {stderr}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 在未安装 node 的精简环境中跳过本单项测试
        }
    }
}
