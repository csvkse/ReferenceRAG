using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    private static Dictionary<string, string> ExtractImportMap(string wwwroot)
    {
        var htmlPath = Path.Combine(wwwroot, "index.html");
        if (!File.Exists(htmlPath)) return new Dictionary<string, string>();

        var html = File.ReadAllText(htmlPath);
        var match = Regex.Match(html, @"<script type=""importmap"">([\s\S]*?)</script>");
        if (!match.Success) return new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("imports", out var importsEl))
            {
                foreach (var prop in importsEl.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static HashSet<string> GetModuleExportedSymbols(string filePath, string wwwroot, Dictionary<string, HashSet<string>> cache, HashSet<string> visited)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (cache.TryGetValue(fullPath, out var cached)) return cached;
        if (!visited.Add(fullPath) || !File.Exists(fullPath)) return new HashSet<string>(StringComparer.Ordinal);

        var source = File.ReadAllText(fullPath);
        var exports = new HashSet<string>(StringComparer.Ordinal);

        if (Regex.IsMatch(source, @"export\s+default\b"))
        {
            exports.Add("default");
        }

        foreach (Match m in Regex.Matches(source, @"export\s+(?:async\s+)?(?:function|class)\s+([a-zA-Z0-9_$]+)"))
        {
            exports.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(source, @"export\s+(?:const|let|var)\s+([^;=]+?)(?:;|=)"))
        {
            foreach (Match id in Regex.Matches(m.Groups[1].Value, @"\b([a-zA-Z0-9_$]+)\b"))
            {
                exports.Add(id.Groups[1].Value);
            }
        }

        foreach (Match m in Regex.Matches(source, @"export\s*\{([\s\S]*?)\}(?:\s*from\s*['""]([^'""]+)['""])?"))
        {
            var list = m.Groups[1].Value;
            var parts = list.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var asMatch = Regex.Match(trimmed, @"(?:^|\b)(?:[a-zA-Z0-9_$]+\s+as\s+)?([a-zA-Z0-9_$]+)$");
                if (asMatch.Success)
                {
                    exports.Add(asMatch.Groups[1].Value);
                }
            }
        }

        foreach (Match m in Regex.Matches(source, @"export\s*\*\s*from\s*['""]([^'""]+)['""]"))
        {
            var reexportSpecifier = m.Groups[1].Value;
            string? reexportPath = null;
            if (reexportSpecifier.StartsWith('.'))
            {
                reexportPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, reexportSpecifier));
            }
            else if (reexportSpecifier.StartsWith('/'))
            {
                reexportPath = Path.GetFullPath(Path.Combine(wwwroot, reexportSpecifier.TrimStart('/')));
            }

            if (reexportPath != null && File.Exists(reexportPath))
            {
                var nested = GetModuleExportedSymbols(reexportPath, wwwroot, cache, visited);
                foreach (var s in nested) exports.Add(s);
            }
        }

        cache[fullPath] = exports;
        return exports;
    }

    [Fact]
    public void AllFrontendViews_EsmModuleImports_MustMatchTargetExportedSymbols()
    {
        var root = FindRepoRoot();
        var wwwroot = Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot");
        var importMap = ExtractImportMap(wwwroot);
        var files = GetFrontendViewFiles(root);
        Assert.NotEmpty(files);

        var cache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        var importRegex = new Regex(@"import\s+(?:([\w$]+)\s*,?\s*)?(?:(?:\*\s+as\s+([\w$]+))|(?=\{)|(\{[\s\S]*?\}))?\s*from\s*['""]([^'""]+)['""]");

        foreach (var file in files)
        {
            var relFile = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);

            foreach (Match match in importRegex.Matches(content))
            {
                var defaultImport = match.Groups[1].Value;
                var namespaceImport = match.Groups[2].Value;
                var namedBlock = match.Groups[3].Value;
                var specifier = match.Groups[4].Value;

                string? resolvedPath = null;
                if (importMap.TryGetValue(specifier, out var mapped))
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(wwwroot, mapped.TrimStart('/')));
                }
                else if (specifier.StartsWith('/'))
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(wwwroot, specifier.TrimStart('/')));
                }
                else if (specifier.StartsWith('.'))
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, specifier));
                }

                if (resolvedPath == null)
                {
                    errors.Add($"[{relFile}] 无法解析模块规范名: '{specifier}'");
                    continue;
                }

                if (!File.Exists(resolvedPath))
                {
                    errors.Add($"[{relFile}] 模块目标文件不存在: '{specifier}' -> {resolvedPath}");
                    continue;
                }

                if (!string.IsNullOrEmpty(namespaceImport) && string.IsNullOrEmpty(defaultImport) && string.IsNullOrEmpty(namedBlock))
                {
                    continue;
                }

                var exports = GetModuleExportedSymbols(resolvedPath, wwwroot, cache, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(defaultImport) && !exports.Contains("default"))
                {
                    errors.Add($"[{relFile}] 模块 '{specifier}' 未提供 default 导出 (导入名: '{defaultImport}')");
                }

                if (!string.IsNullOrEmpty(namedBlock))
                {
                    var clean = namedBlock.Trim('{', '}');
                    var items = clean.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));
                    foreach (var item in items)
                    {
                        var parts = Regex.Split(item, @"\s+as\s+");
                        var importedName = parts[0].Trim();
                        if (!exports.Contains(importedName))
                        {
                            errors.Add($"[{relFile}] 模块 '{specifier}' 未提供名为 '{importedName}' 的导出符号 (浏览器端将抛出 SyntaxError 启动失败)");
                        }
                    }
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"发现 {errors.Count} 处 ESM 模块导入/导出契约违背异常:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void AllFrontendViews_NaiveUiSpaceSize_MustBeValidEnumOrNumber()
    {
        var root = FindRepoRoot();
        var files = GetFrontendViewFiles(root);
        Assert.NotEmpty(files);

        var errors = new List<string>();
        var validSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "small", "medium", "large" };

        var tagSpaceRegex = new Regex(@"<n-space\b([^>]*)>", RegexOptions.IgnoreCase);
        var tagSizeRegex = new Regex(@"\bsize=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        var hSpaceRegex = new Regex(@"h\s*\(\s*NSpace\s*,\s*\{([^}]*)\}");
        var hSizeRegex = new Regex(@"\bsize\s*:\s*['""]([^'""]+)['""]");

        foreach (var file in files)
        {
            var relFile = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);

            // 1. Template <n-space ... size="..." ...>
            foreach (Match tm in tagSpaceRegex.Matches(content))
            {
                var attrs = tm.Groups[1].Value;
                var sm = tagSizeRegex.Match(attrs);
                if (sm.Success)
                {
                    var val = sm.Groups[1].Value.Trim();
                    if (!validSizes.Contains(val) && !double.TryParse(val, out _))
                    {
                        errors.Add($"[{relFile}] <n-space> 存在非法 size=\"{val}\"，Naive UI Space 仅支持 'small'|'medium'|'large' 或数字，传 tiny 会导致运行时异常 (reading 'split'/'horizontal')");
                    }
                }
            }

            // 2. Render h(NSpace, { size: '...' })
            foreach (Match hm in hSpaceRegex.Matches(content))
            {
                var props = hm.Groups[1].Value;
                var sm = hSizeRegex.Match(props);
                if (sm.Success)
                {
                    var val = sm.Groups[1].Value.Trim();
                    if (!validSizes.Contains(val) && !double.TryParse(val, out _))
                    {
                        errors.Add($"[{relFile}] h(NSpace, ...) 存在非法 size: '{val}'，Naive UI Space 仅支持 'small'|'medium'|'large' 或数字，传 tiny 会导致运行时异常 (reading 'split'/'horizontal')");
                    }
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"发现 {errors.Count} 处 Naive UI NSpace size 属性违规缺陷:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void AllFrontendViews_FlexScrollContainers_MustNotShrinkChildElements()
    {
        var root = FindRepoRoot();
        var files = GetFrontendViewFiles(root);
        Assert.NotEmpty(files);

        var errors = new List<string>();
        var flexScrollRegex = new Regex(@"<([a-zA-Z0-9_-]+)\b([^>]*style=[""'][^""']*[""'][^>]*)>", RegexOptions.IgnoreCase);
        var styleRegex = new Regex(@"style=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        var childDivRegex = new Regex(@"<div\b([^>]*style=[""'][^""']*[""'][^>]*)>", RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var relFile = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);

            foreach (Match match in flexScrollRegex.Matches(content))
            {
                var tagName = match.Groups[1].Value;
                var sm = styleRegex.Match(match.Groups[2].Value);
                if (!sm.Success) continue;
                var style = sm.Groups[1].Value;

                var isFlex = Regex.IsMatch(style, @"display:\s*flex", RegexOptions.IgnoreCase);
                var isCol = Regex.IsMatch(style, @"flex-direction:\s*column", RegexOptions.IgnoreCase);
                var isScroll = Regex.IsMatch(style, @"overflow(?:-y)?:\s*auto", RegexOptions.IgnoreCase);
                var hasHeight = Regex.IsMatch(style, @"(?:max-height|height):", RegexOptions.IgnoreCase);

                if (isFlex && isCol && isScroll && hasHeight)
                {
                    var startPos = match.Index + match.Length;
                    var length = Math.Min(3000, content.Length - startPos);
                    var snippet = content.Substring(startPos, length);

                    foreach (Match childMatch in childDivRegex.Matches(snippet))
                    {
                        var csm = styleRegex.Match(childMatch.Groups[1].Value);
                        if (!csm.Success) continue;
                        var cStyle = csm.Groups[1].Value;

                        var hasOverflowHidden = Regex.IsMatch(cStyle, @"overflow:\s*hidden", RegexOptions.IgnoreCase);
                        var hasFlexShrinkZero = Regex.IsMatch(cStyle, @"flex-shrink:\s*0", RegexOptions.IgnoreCase) ||
                                                Regex.IsMatch(cStyle, @"flex:\s*0\s+0", RegexOptions.IgnoreCase) ||
                                                Regex.IsMatch(cStyle, @"flex:\s*none", RegexOptions.IgnoreCase);

                        if (hasOverflowHidden && !hasFlexShrinkZero)
                        {
                            errors.Add($"[{relFile}] <{tagName}> 纵向滚动容器 (flex-direction: column + overflow-y: auto) 内的子级 <div> 具有 'overflow: hidden' 但缺失 'flex-shrink: 0'，导致 Flexbox 将子项高度压缩至 0 产生重叠挤压缺陷！");
                            break;
                        }
                    }
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"发现 {errors.Count} 处 Flex 滚动容器子元素收缩重叠缺陷:\n" + string.Join("\n", errors));
    }
}
