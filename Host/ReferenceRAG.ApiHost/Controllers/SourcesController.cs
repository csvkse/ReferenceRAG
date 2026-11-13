using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 源文件夹管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SourcesController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IVectorStore _vectorStore;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IFileMonitorService _fileMonitor;
    private readonly ILogger<SourcesController> _logger;
    private readonly ICatalogStore? _catalogStore;
    private readonly IKnowledgeBaseService? _kbService;
    private readonly ISourceLinkEventPublisher? _eventPublisher;
    private readonly ISourceLinkProjectionCache? _projectionCache;

    public SourcesController(
        ConfigManager configManager,
        IVectorStore vectorStore,
        IFileIndexPipeline pipeline,
        IFileMonitorService fileMonitor,
        ILogger<SourcesController> logger,
        ICatalogStore? catalogStore = null,
        IKnowledgeBaseService? kbService = null,
        ISourceLinkEventPublisher? eventPublisher = null,
        ISourceLinkProjectionCache? projectionCache = null)
    {
        _configManager = configManager;
        _vectorStore = vectorStore;
        _pipeline = pipeline;
        _fileMonitor = fileMonitor;
        _logger = logger;
        _catalogStore = catalogStore;
        _kbService = kbService;
        _eventPublisher = eventPublisher;
        _projectionCache = projectionCache;
    }

    /// <summary>写事务完成后同步发布领域事件；可选依赖为空时（单测/降级）静默跳过。</summary>
    private void Publish(SourceLinkEventKind kind, string path, string? sourceName = null,
        string? kbId = null, string? kbName = null, string? connectorId = null, string? detail = null)
    {
        _eventPublisher?.Publish(new SourceLinkEvent
        {
            Kind = kind,
            Path = path,
            SourceName = sourceName,
            KnowledgeBaseId = kbId,
            KnowledgeBaseName = kbName,
            ConnectorId = connectorId,
            Detail = detail
        });
    }

    /// <summary>一次性枚举所有 KB 的连接器并投影为读模型；供 GetAll 与投影缓存共用。</summary>
    private async Task<IReadOnlyList<ConnectorProjection>> LoadConnectorProjections(CancellationToken ct)
    {
        if (_catalogStore == null) return Array.Empty<ConnectorProjection>();
        var result = new List<ConnectorProjection>();
        foreach (var kb in await _catalogStore.ListKnowledgeBasesAsync(ct))
        {
            foreach (var conn in await _catalogStore.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct))
            {
                result.Add(new ConnectorProjection(
                    kb.Id, kb.Name, conn.Id, conn.TargetPath,
                    conn.Enabled, conn.FilePattern, conn.UpdatedAt));
            }
        }
        return result;
    }

    /// <summary>
    /// 获取所有源
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SourceDetail>>> GetAll()
    {
        var config = _configManager.Load();
        var statsMap = (await _vectorStore.GetSourceStatsAsync())
            .ToDictionary(s => s.Source, StringComparer.OrdinalIgnoreCase);

        var sources = config.Sources.Select(s =>
        {
            statsMap.TryGetValue(s.Name, out var stat);
            return new SourceDetail
            {
                Name = s.Name,
                Path = s.Path,
                Type = s.Type.ToString(),
                Enabled = s.Enabled,
                FilePatterns = s.FilePatterns,
                FileCount = stat?.FileCount ?? 0,
                ChunkCount = stat?.ChunkCount ?? 0,
                LastIndexed = stat?.LastIndexed
            };
        }).ToList();

        var linkMap = new Dictionary<string, List<LinkedKnowledgeBaseInfo>>(StringComparer.OrdinalIgnoreCase);

        // 连接器投影带缓存（由领域事件显式失效 + 30s TTL 兜底），避免每次 GetAll 都做 N+1 catalog 遍历。
        // 未注入缓存时（单测 / 降级）走原直读路径，行为与 P0 一致。
        IReadOnlyList<ConnectorProjection> projections = Array.Empty<ConnectorProjection>();
        if (_catalogStore != null)
        {
            try
            {
                projections = _projectionCache != null
                    ? await _projectionCache.GetOrLoadAsync(LoadConnectorProjections)
                    : await LoadConnectorProjections(default);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SourcesController] 从 CatalogStore 获取连接器列表异常");
            }
        }

        foreach (var conn in projections)
        {
            var key = NormalizePathKey(conn.TargetPath);
            if (!linkMap.TryGetValue(key, out var links))
            {
                links = new List<LinkedKnowledgeBaseInfo>();
                linkMap[key] = links;
            }
            links.Add(new LinkedKnowledgeBaseInfo
            {
                Id = conn.KnowledgeBaseId,
                Name = conn.KnowledgeBaseName,
                ConnectorId = conn.ConnectorId
            });

            if (!sources.Any(s => s.Path.Equals(conn.TargetPath, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new SourceDetail
                {
                    Name = BuildConnectorDisplayName(conn),
                    Path = conn.TargetPath,
                    Type = "KnowledgeBaseConnector",
                    Enabled = conn.Enabled,
                    FilePatterns = SplitPatterns(conn.FilePattern),
                    FileCount = 0,
                    ChunkCount = 0,
                    LastIndexed = conn.UpdatedAt,
                    IsConnector = true,
                    KnowledgeBaseId = conn.KnowledgeBaseId,
                    ConnectorId = conn.ConnectorId,
                    Origin = "KnowledgeBaseOnly"
                });
            }
        }

        // 回填每个源/连接器行已挂载的知识库（跨 KB 允许同一路径多次挂载）
        foreach (var source in sources)
        {
            source.LinkedKnowledgeBases = linkMap.TryGetValue(NormalizePathKey(source.Path), out var links)
                ? links
                : new List<LinkedKnowledgeBaseInfo>();
        }

        return Ok(sources);
    }

    /// <summary>
    /// 获取所有路径（源和文件夹）
    /// </summary>
    /// <remarks>
    /// 返回所有已配置的源及其包含的文件夹路径列表
    /// </remarks>
    [HttpGet("~/api/paths")]
    public async Task<ActionResult<PathsResponse>> GetPaths(
        [FromQuery] bool enabledOnly = true,
        [FromQuery] string? kbId = null,
        [FromQuery] string? kbIds = null)
    {
        if (_kbService != null)
        {
            var requestedKbIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(kbId)) requestedKbIds.Add(kbId);
            if (!string.IsNullOrWhiteSpace(kbIds))
            {
                requestedKbIds.AddRange(kbIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (requestedKbIds.Count > 0)
            {
                var kbPaths = await _kbService.GetPathsAsync(requestedKbIds);
                return Ok(kbPaths);
            }

            var allKbs = await _kbService.ListKnowledgeBasesAsync();
            if (allKbs.Any(k => k.Enabled))
            {
                var allKbPaths = await _kbService.GetPathsAsync(Array.Empty<string>());
                return Ok(allKbPaths);
            }
        }

        var config = _configManager.Load();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var sourcesToInclude = enabledOnly
            ? config.Sources.Where(s => s.Enabled).ToList()
            : config.Sources;

        var response = new PathsResponse
        {
            Sources = sourcesToInclude.Select(s =>
            {
                var sourceFiles = fileList.Where(f => string.Equals(f.Source, s.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                // 从文件路径中提取唯一的文件夹路径
                var folders = sourceFiles
                    .Select(f =>
                    {
                        // 获取文件的父文件夹路径
                        var parentFolder = Path.GetDirectoryName(f.Path);
                        return parentFolder;
                    })
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct()
                    .OrderBy(p => p)
                    .ToList();

                return new SourcePathInfo
                {
                    Name = s.Name,
                    RootPath = s.Path,
                    Folders = folders
                };
            }).ToList()
        };

        return Ok(response);
    }

    private static SourceFolder? FindSource(ObsidianRagConfig config, string name)
    {
        var decoded = Uri.UnescapeDataString(name);
        return config.Sources.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(decoded, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析源标识：优先 config.Sources，其次知识库连接器（投影为只读 SourceFolder）。
    /// 连接器不在 config.Sources 中，若不显式解析，/Sources/{name} 系列接口会误报「源不存在」。
    /// </summary>
    private async Task<(SourceFolder? Source, ConnectorDefinition? Connector, KnowledgeBaseDefinition? KnowledgeBase)>
        ResolveSourceAsync(string name, CancellationToken ct = default)
    {
        var config = _configManager.Load();
        var source = FindSource(config, name);
        if (source != null) return (source, null, null);

        if (_catalogStore == null) return (null, null, null);

        var decoded = Uri.UnescapeDataString(name);
        try
        {
            foreach (var kb in await _catalogStore.ListKnowledgeBasesAsync(ct))
            {
                foreach (var conn in await _catalogStore.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct))
                {
                    var displayName = BuildConnectorDisplayName(kb, conn);
                    if (displayName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || displayName.Equals(decoded, StringComparison.OrdinalIgnoreCase)
                        || conn.TargetPath.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || conn.TargetPath.Equals(decoded, StringComparison.OrdinalIgnoreCase))
                    {
                        var projected = new SourceFolder
                        {
                            Name = displayName,
                            Path = conn.TargetPath,
                            Enabled = conn.Enabled,
                            FilePatterns = SplitPatterns(conn.FilePattern),
                            Type = SourceType.Markdown
                        };
                        return (projected, conn, kb);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SourcesController] 解析知识库连接器失败: {Name}", name);
        }

        return (null, null, null);
    }

    /// <summary>
    /// 路径去重键：统一斜杠、去尾斜杠、小写。与 CatalogStore 的 path_key 保持一致。
    /// </summary>
    private static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    private static string BuildConnectorDisplayName(KnowledgeBaseDefinition kb, ConnectorDefinition conn)
    {
        var folder = Path.GetFileName(conn.TargetPath);
        return string.IsNullOrWhiteSpace(kb.Name)
            ? (string.IsNullOrWhiteSpace(folder) ? conn.Id : folder)
            : $"{kb.Name} - {folder}";
    }

    private static string BuildConnectorDisplayName(ConnectorProjection projection)
    {
        var folder = Path.GetFileName(projection.TargetPath);
        return string.IsNullOrWhiteSpace(projection.KnowledgeBaseName)
            ? (string.IsNullOrWhiteSpace(folder) ? projection.ConnectorId : folder)
            : $"{projection.KnowledgeBaseName} - {folder}";
    }

    private static List<string> SplitPatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string> { "*.md", "*.txt" };
        var patterns = raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return patterns.Count > 0 ? patterns : new List<string> { "*.md", "*.txt" };
    }

    /// <summary>
    /// 连接器是知识库的从属实体，写操作必须回到知识库管理，返回 409 并附带定位信息。
    /// </summary>
    private ConflictObjectResult ConnectorWriteConflict(
        string name, KnowledgeBaseDefinition? kb, ConnectorDefinition connector)
    {
        return Conflict(new
        {
            error = $"'{name}' 是知识库连接器，请在知识库管理中执行该操作",
            code = "ConnectorManagedByKnowledgeBase",
            knowledgeBaseId = kb?.Id,
            knowledgeBaseName = kb?.Name,
            connectorId = connector.Id,
            connectorPath = connector.TargetPath
        });
    }

    /// <summary>
    /// 连接器文件列表直接按目录枚举，不依赖向量库的 source 字段命名。
    /// </summary>
    private List<FileDetail> EnumerateConnectorFiles(SourceFolder source, int page, int pageSize)
    {
        var normalizedPath = PathUtility.NormalizePath(source.Path);
        if (!Directory.Exists(normalizedPath)) return new List<FileDetail>();

        var patterns = (source.FilePatterns?.Count > 0)
            ? source.FilePatterns
            : new List<string> { "*.md", "*.txt" };
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 500);

        return Directory.EnumerateFiles(normalizedPath, "*", options)
            .Where(f => patterns.Any(p => MatchesPattern(f, p)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new FileDetail
                {
                    Path = f.Replace('\\', '/'),
                    ChunkCount = 0,
                    LastModified = info.LastWriteTime,
                    Hash = ""
                };
            })
            .ToList();
    }

    /// <summary>
    /// 获取单个源详情
    /// </summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<SourceDetail>> Get(string name)
    {
        var (source, connector, knowledgeBase) = await ResolveSourceAsync(name);

        if (source == null)
            return NotFound(new { error = $"源 '{name}' 不存在" });

        var statsMap = (await _vectorStore.GetSourceStatsAsync())
            .ToDictionary(s => s.Source, StringComparer.OrdinalIgnoreCase);
        statsMap.TryGetValue(source.Name, out var stat);

        return Ok(new SourceDetail
        {
            Name = source.Name,
            Path = source.Path,
            Type = connector != null ? "KnowledgeBaseConnector" : source.Type.ToString(),
            Enabled = source.Enabled,
            FilePatterns = source.FilePatterns,
            FileCount = stat?.FileCount ?? 0,
            ChunkCount = stat?.ChunkCount ?? 0,
            LastIndexed = stat?.LastIndexed,
            IsConnector = connector != null,
            KnowledgeBaseId = knowledgeBase?.Id,
            ConnectorId = connector?.Id
        });
    }

    /// <summary>
    /// 获取源文件列表
    /// </summary>
    [HttpGet("{name}/files")]
    public async Task<ActionResult<List<FileDetail>>> GetFiles(string name, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var (source, connector, _) = await ResolveSourceAsync(name);

        if (source == null)
            return NotFound(new { error = $"源 '{name}' 不存在" });

        // 连接器不在 config.Sources 中，索引侧的 source 字段命名也不保证一致，
        // 因此只读文件列表直接按目录枚举，避免「源存在但文件为空」的误导。
        if (connector != null)
            return Ok(EnumerateConnectorFiles(source, page, pageSize));

        var files = await _vectorStore.GetFilesBySourceAsync(source.Name, page, pageSize);
        var sourceFiles = files.Select(f => new FileDetail
        {
            Path = f.Path,
            ChunkCount = f.ChunkCount,
            LastModified = f.ModifiedAt ?? DateTime.MinValue,
            Hash = f.ContentHash.Length >= 8 ? f.ContentHash[..8] : f.ContentHash
        }).ToList();

        return Ok(sourceFiles);
    }

    /// <summary>
    /// 添加源
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SourceFolder>> Add([FromBody] AddSourceRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
        {
            return BadRequest(new { error = "路径不能为空" });
        }

        // Path traversal prevention
        if (request.Path.Contains(".."))
        {
            return BadRequest(new { error = "路径不能包含 '..' 目录遍历字符" });
        }

        if (!Path.IsPathRooted(request.Path))
        {
            return BadRequest(new { error = "路径必须是绝对路径" });
        }

        var fullPath = Path.GetFullPath(request.Path);
        if (!Directory.Exists(fullPath))
        {
            return BadRequest(new { error = $"目录不存在: {fullPath}" });
        }

        var config = _configManager.Load();

        // 检查是否已存在（使用不区分大小写的路径比较）
        var existingSource = config.Sources.FirstOrDefault(s =>
            s.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(request.Name) && s.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)));

        if (existingSource != null)
        {
            return Conflict(new { error = $"源已存在: {existingSource.Name} ({existingSource.Path})" });
        }

        var source = new SourceFolder
        {
            Path = fullPath,
            Name = request.Name ?? Path.GetFileName(request.Path) ?? "新源",
            Type = request.Type ?? SourceType.Markdown,
            Enabled = true,
            FilePatterns = (request.FilePatterns?.Length > 0) ? request.FilePatterns.ToList() : new List<string> { "*.md", "*.txt" },
            ExcludeDirs = new List<string> { ".git", "node_modules" }
        };

        // Obsidian 类型自动添加排除目录
        if (source.Type == SourceType.Obsidian)
        {
            source.ExcludeDirs.AddRange(new[] { ".obsidian", ".trash" });
        }

        try
        {
            _configManager.AddSource(source);
            if (source.Enabled)
                _fileMonitor.AddSource(source.Path, source.Name, source.FilePatterns);

            // 显式挂载：优先使用请求指定的知识库，缺省挂到第一个启用知识库。
            // 改为同步 + 幂等（EnsureConnectorAsync），避免 fire-and-forget 丢失与重复连接器。
            if (_kbService != null)
            {
                try
                {
                    var kbs = await _kbService.ListKnowledgeBasesAsync();
                    var targetKb = !string.IsNullOrWhiteSpace(request.KnowledgeBaseId)
                        ? kbs.FirstOrDefault(k => k.Id == request.KnowledgeBaseId)
                        : (kbs.FirstOrDefault(k => k.Enabled) ?? kbs.FirstOrDefault());

                    if (targetKb != null)
                    {
                        var (connector, created) = await _kbService.EnsureConnectorAsync(
                            targetKb.Id,
                            "LocalFolder",
                            source.Path,
                            string.Join(";", source.FilePatterns ?? new List<string>()));
                        if (created)
                        {
                            Publish(SourceLinkEventKind.SourceLinked, source.Path, source.Name,
                                targetKb.Id, targetKb.Name, connector.Id, "add-source");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 源已创建成功；连接器挂载失败不回滚，但必须留痕
                    _logger.LogWarning(ex, "[SourcesController] 同步创建知识库连接器异常");
                }
            }

            _logger.LogInformation("已添加源: {Name} ({Path})", source.Name, source.Path);
            return CreatedAtAction(nameof(Get), new { name = source.Name }, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加源失败: {Path}", request.Path);
            return StatusCode(500, new { error = "添加源失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 将已有源挂载到知识库（幂等：同一知识库内同路径已存在时返回 already-linked）。
    /// 允许同一物理路径挂到多个知识库。
    /// </summary>
    [HttpPost("{name}/link")]
    public async Task<ActionResult> Link(string name, [FromBody] LinkSourceRequest? request = null)
    {
        var (source, _, _) = await ResolveSourceAsync(name);
        if (source == null)
            return NotFound(new { error = $"源 '{name}' 不存在" });

        if (_kbService == null)
            return StatusCode(503, new { error = "知识库服务不可用" });

        var kbs = await _kbService.ListKnowledgeBasesAsync();
        var targetKb = !string.IsNullOrWhiteSpace(request?.KnowledgeBaseId)
            ? kbs.FirstOrDefault(k => k.Id == request!.KnowledgeBaseId)
            : (kbs.FirstOrDefault(k => k.Enabled) ?? kbs.FirstOrDefault());

        if (targetKb == null)
            return BadRequest(new { error = "尚未创建可用的知识库" });

        var (connector, created) = await _kbService.EnsureConnectorAsync(
            targetKb.Id,
            "LocalFolder",
            source.Path,
            string.Join(";", source.FilePatterns ?? new List<string>()));

        if (created)
        {
            Publish(SourceLinkEventKind.SourceLinked, source.Path, source.Name,
                targetKb.Id, targetKb.Name, connector.Id, "link");
            return StatusCode(StatusCodes.Status201Created, new
            {
                status = "created",
                connectorId = connector.Id,
                knowledgeBaseId = targetKb.Id,
                knowledgeBaseName = targetKb.Name
            });
        }

        return Ok(new
        {
            status = "already-linked",
            connectorId = connector.Id,
            knowledgeBaseId = targetKb.Id,
            knowledgeBaseName = targetKb.Name
        });
    }

    /// <summary>
    /// 解除源与知识库的挂载关系（不删除 config.Sources 与物理文件）。
    /// reclaim=retain 保留索引数据，reclaim=delete 级联清理该连接器数据。
    /// </summary>
    [HttpDelete("{name}/link")]
    public async Task<ActionResult> Unlink(
        string name,
        [FromQuery] string knowledgeBaseId,
        [FromQuery] string reclaim = "retain")
    {
        if (string.IsNullOrWhiteSpace(knowledgeBaseId))
            return BadRequest(new { error = "knowledgeBaseId 不能为空" });

        var (source, _, _) = await ResolveSourceAsync(name);
        if (source == null)
            return NotFound(new { error = $"源 '{name}' 不存在" });

        if (_kbService == null || _catalogStore == null)
            return StatusCode(503, new { error = "知识库服务不可用" });

        var key = NormalizePathKey(source.Path);
        var connectors = await _catalogStore.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId);
        var target = connectors.FirstOrDefault(c => NormalizePathKey(c.TargetPath) == key);
        if (target == null)
            return NotFound(new { error = $"该路径未挂载到知识库 {knowledgeBaseId}" });

        await _kbService.DeleteConnectorAsync(knowledgeBaseId, target.Id, cascade: reclaim == "delete");
        Publish(SourceLinkEventKind.ConnectorDetached, source.Path, source.Name,
            knowledgeBaseId, null, target.Id, $"unlink:{reclaim}");
        return NoContent();
    }

    /// <summary>
    /// 更新源
    /// </summary>
    [HttpPut("{name}")]
    public async Task<ActionResult> Update(string name, [FromBody] UpdateSourceRequest request)
    {
        var config = _configManager.Load();
        var source = FindSource(config, name);

        if (source == null)
        {
            var (_, connector, kb) = await ResolveSourceAsync(name);
            if (connector != null) return ConnectorWriteConflict(name, kb, connector);
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        if (request.Name != null && !request.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "不支持重命名源，请删除后重新添加" });
        }

        if (request.Enabled.HasValue)
        {
            source.Enabled = request.Enabled.Value;
        }

        if (request.FilePatterns != null)
        {
            source.FilePatterns = (request.FilePatterns.Length > 0) ? request.FilePatterns.ToList() : new List<string> { "*.md", "*.txt" };
        }

        _configManager.Save(config);
        _fileMonitor.RemoveSource(source.Name);
        if (source.Enabled)
            _fileMonitor.AddSource(source.Path, source.Name, source.FilePatterns);

        return Ok(source);
    }

    /// <summary>
    /// 删除源
    /// </summary>
    [HttpDelete("{name}")]
    public async Task<ActionResult> Delete(string name, [FromQuery] bool deleteData = false, [FromQuery] bool cascade = false)
    {
        var config = _configManager.Load();
        var source = FindSource(config, name);

        if (source == null)
        {
            var (_, connector, kb) = await ResolveSourceAsync(name);
            if (connector != null) return ConnectorWriteConflict(name, kb, connector);
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        try
        {
            // 找出该路径已挂载的知识库连接器；默认要求显式解绑，cascade=true 时一并解绑
            var linked = new List<(string KnowledgeBaseId, string KnowledgeBaseName, string ConnectorId)>();
            if (_catalogStore != null)
            {
                var key = NormalizePathKey(source.Path);
                foreach (var kb in await _catalogStore.ListKnowledgeBasesAsync())
                {
                    foreach (var conn in await _catalogStore.GetConnectorsByKnowledgeBaseAsync(kb.Id))
                    {
                        if (NormalizePathKey(conn.TargetPath) == key)
                        {
                            linked.Add((kb.Id, kb.Name, conn.Id));
                        }
                    }
                }
            }

            if (linked.Count > 0 && !cascade)
            {
                return Conflict(new
                {
                    error = $"源 '{name}' 已挂载到 {linked.Count} 个知识库，请先解绑，或使用 cascade=true 一并解绑",
                    code = "SourceLinkedToKnowledgeBases",
                    linkedKnowledgeBases = linked.Select(x => new
                    {
                        id = x.KnowledgeBaseId,
                        name = x.KnowledgeBaseName,
                        connectorId = x.ConnectorId
                    })
                });
            }

            if (linked.Count > 0)
            {
                if (_kbService == null)
                    return StatusCode(503, new { error = "知识库服务不可用，无法解绑关联连接器" });

                foreach (var link in linked)
                {
                    await _kbService.DeleteConnectorAsync(
                        link.KnowledgeBaseId,
                        link.ConnectorId,
                        cascade: deleteData);
                    Publish(SourceLinkEventKind.ConnectorDetached, source.Path, source.Name,
                        link.KnowledgeBaseId, link.KnowledgeBaseName, link.ConnectorId,
                        $"cascade-delete:deleteData={deleteData}");
                }
            }

            if (deleteData)
                await _pipeline.DeleteSourceAsync(source.Name);

            _configManager.RemoveSource(source.Name);
            _fileMonitor.RemoveSource(source.Name);
            Publish(SourceLinkEventKind.SourceRemoved, source.Path, source.Name,
                detail: $"removed:deleteData={deleteData},detached={linked.Count}");

            _logger.LogInformation("已删除源: {Name} (deleteData={DeleteData})", source.Name, deleteData);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除源失败: {Name}", source.Name);
            return StatusCode(500, new { error = $"删除源失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 启用/禁用源
    /// </summary>
    [HttpPatch("{name}/toggle")]
    public async Task<ActionResult> Toggle(string name, [FromBody] ToggleRequest request)
    {
        var config = _configManager.Load();
        var source = FindSource(config, name);
        if (source == null)
        {
            var (_, connector, kb) = await ResolveSourceAsync(name);
            if (connector != null) return ConnectorWriteConflict(name, kb, connector);
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        _configManager.ToggleSource(source.Name, request.Enabled);
        source = FindSource(_configManager.Load(), source.Name);
        if (source != null)
        {
            _fileMonitor.RemoveSource(source.Name);
            if (request.Enabled)
                _fileMonitor.AddSource(source.Path, source.Name, source.FilePatterns);
        }
        return Ok();
    }

    /// <summary>
    /// 扫描源文件（不索引）
    /// </summary>
    [HttpGet("{name}/scan")]
    public async Task<ActionResult<ScanResult>> Scan(string name)
    {
        var (source, _, _) = await ResolveSourceAsync(name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        // 转换路径格式（WSL 环境下将 Windows 路径转为 /mnt/x/ 格式）
        var normalizedPath = PathUtility.NormalizePath(source.Path);
        if (!Directory.Exists(normalizedPath))
        {
            return BadRequest(new { error = $"源目录不存在或无法访问: {source.Path}" });
        }

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var allDiskFiles = new List<string>();
        try
        {
            allDiskFiles = Directory.EnumerateFiles(normalizedPath, "*", enumOptions).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描目录失败: {Path}", normalizedPath);
        }

        var patterns = (source.FilePatterns?.Count > 0) ? source.FilePatterns : new List<string> { "*.md", "*.txt" };
        var excludeDirs = source.ExcludeDirs ?? new List<string>();

        var matchedFiles = allDiskFiles
            .Where(f => patterns.Any(p => MatchesPattern(f, p)))
            .Where(f => !excludeDirs.Any(d => f.Contains(d, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var diskExtensions = allDiskFiles
            .Select(f => Path.GetExtension(f).ToLowerInvariant())
            .Where(ext => !string.IsNullOrEmpty(ext))
            .GroupBy(ext => ext)
            .ToDictionary(g => g.Key, g => g.Count());

        var matchedExtensions = matchedFiles
            .Select(f => Path.GetExtension(f).ToLowerInvariant())
            .Where(ext => !string.IsNullOrEmpty(ext))
            .GroupBy(ext => ext)
            .ToDictionary(g => g.Key, g => g.Count());

        long totalSize = 0;
        var fileItems = new List<FileItem>();
        foreach (var f in matchedFiles.Take(100))
        {
            long size = 0;
            DateTime mod = DateTime.MinValue;
            try
            {
                var fi = new System.IO.FileInfo(f);
                size = fi.Length;
                mod = fi.LastWriteTime;
            }
            catch { }
            totalSize += size;
            fileItems.Add(new FileItem
            {
                Path = Path.GetRelativePath(normalizedPath, f).Replace('\\', '/'),
                Size = size,
                Modified = mod
            });
        }

        string msg;
        if (allDiskFiles.Count == 0)
        {
            msg = "磁盘目录为空或服务没有读取权限 (请检查 Docker 目录映射与 Linux 权限)";
        }
        else if (matchedFiles.Count == 0)
        {
            var diskExtSummary = string.Join(", ", diskExtensions.Select(kv => $"{kv.Key} ({kv.Value}个)"));
            msg = $"磁盘探查到 {allDiskFiles.Count} 个文件，但无文件匹配当前规则 [{string.Join(", ", patterns)}]！发现的文件扩展名: {diskExtSummary}。请编辑源规则以包含这些扩展名。";
        }
        else
        {
            msg = $"成功匹配到 {matchedFiles.Count} 个文件 (磁盘总文件数: {allDiskFiles.Count})";
        }

        var result = new ScanResult
        {
            TotalFiles = matchedFiles.Count,
            TotalSize = totalSize,
            Extensions = matchedExtensions,
            Files = fileItems,
            TotalDiskFiles = allDiskFiles.Count,
            DiskExtensions = diskExtensions,
            Message = msg
        };

        return Ok(result);
    }


    /// <summary>
    /// 按行范围批量获取文件分段内容
    /// </summary>
    [HttpPost("file/lines")]
    public async Task<ActionResult<FileLinesResponse>> GetFileLinesBatch([FromBody] FileLinesRequest request)
    {
        if (request.Items == null || request.Items.Count == 0)
            return BadRequest(new { error = "items 不能为空" });

        var config = _configManager.Load();
        var sourcePaths = config.Sources
            .Select(s => Path.GetFullPath(s.Path))
            .ToList();

        var results = new List<FileLinesResult>();

        const int maxItems = 20;
        if (request.Items.Count > maxItems)
            return BadRequest(new { error = $"单次最多 {maxItems} 条，当前 {request.Items.Count} 条" });

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Contains(".."))
            {
                results.Add(new FileLinesResult { Path = item.Path ?? "", Error = "路径无效" });
                continue;
            }

            if (item.StartLine < 0 || item.EndLine < 0)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "startLine / endLine 不能为负数" });
                continue;
            }

            if (item.StartLine > 0 && item.EndLine > 0 && item.StartLine > item.EndLine)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = $"startLine ({item.StartLine}) 不能大于 endLine ({item.EndLine})" });
                continue;
            }

            var normalizedPath = Path.GetFullPath(item.Path);

            if (!sourcePaths.Any(sp => normalizedPath.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "路径不在任何已注册源的范围内" });
                continue;
            }

            var file = await _vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await _vectorStore.GetFileByPathAsync(item.Path);

            if (file == null)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "文件未被索引" });
                continue;
            }

            var allChunks = await _vectorStore.GetChunksByFileAsync(file.Id);

            // 行范围过滤：startLine=0 且 endLine=0 时返回全部
            var filtered = allChunks.OrderBy(c => c.ChunkOrder);
            var hasRange = item.StartLine > 0 || item.EndLine > 0;
            if (hasRange)
            {
                var start = item.StartLine > 0 ? item.StartLine : 1;
                var end = item.EndLine > 0 ? item.EndLine : int.MaxValue;
                filtered = filtered.Where(c => c.StartLine <= end && c.EndLine >= start)
                                   .OrderBy(c => c.ChunkOrder);
            }

            results.Add(new FileLinesResult
            {
                Path = file.Path,
                Title = file.Title,
                Source = file.Source,
                RequestedRange = hasRange
                    ? new LineRange { StartLine = item.StartLine, EndLine = item.EndLine }
                    : null,
                Chunks = filtered.Select(c => new FileChunkItem
                {
                    Index = c.ChunkIndex,
                    HeadingPath = c.HeadingPath,
                    StartLine = c.StartLine,
                    EndLine = c.EndLine,
                    Content = c.Content
                }).ToList()
            });
        }

        return Ok(new FileLinesResponse { Results = results });
    }

    /// <summary>
    /// 批量获取文件结构信息（章节目录 + 行号，不含正文内容）
    /// </summary>
    [HttpPost("files/info")]
    public async Task<ActionResult<FilesInfoResponse>> GetFilesInfo([FromBody] FilesInfoRequest request)
    {
        if (request.Paths == null || request.Paths.Count == 0)
            return BadRequest(new { error = "paths 不能为空" });

        const int maxPaths = 20;
        if (request.Paths.Count > maxPaths)
            return BadRequest(new { error = $"单次最多 {maxPaths} 条，当前 {request.Paths.Count} 条" });

        var config = _configManager.Load();
        var sourcePaths = config.Sources
            .Select(s => Path.GetFullPath(s.Path))
            .ToList();

        var results = new List<FileInfoResult>();

        foreach (var rawPath in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains(".."))
            {
                results.Add(new FileInfoResult { Path = rawPath ?? "", Error = "路径无效" });
                continue;
            }

            var normalizedPath = Path.GetFullPath(rawPath);

            if (!sourcePaths.Any(sp => normalizedPath.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new FileInfoResult { Path = rawPath, Error = "路径不在任何已注册源的范围内" });
                continue;
            }

            var file = await _vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await _vectorStore.GetFileByPathAsync(rawPath);

            if (file == null)
            {
                results.Add(new FileInfoResult { Path = rawPath, Error = "文件未被索引" });
                continue;
            }

            var chunks = (await _vectorStore.GetChunksByFileAsync(file.Id))
                .OrderBy(c => c.ChunkOrder)
                .ToList();

            results.Add(new FileInfoResult
            {
                Path = file.Path,
                Title = file.Title,
                Source = file.Source,
                TotalChunks = chunks.Count,
                TotalLines = chunks.Count > 0 ? chunks.Max(c => c.EndLine) : 0,
                Sections = chunks.Select(c => new FileSectionItem
                {
                    Index = c.ChunkIndex,
                    HeadingPath = c.HeadingPath,
                    StartLine = c.StartLine,
                    EndLine = c.EndLine
                }).ToList()
            });
        }

        return Ok(new FilesInfoResponse { Results = results });
    }

    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        if (p == "*" || p == "*.*") return true;

        var fileName = Path.GetFileName(filePath);
        if (p.StartsWith("*."))
        {
            var ext = p[1..];
            return fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        if (p.StartsWith('.'))
        {
            return fileName.EndsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        if (p.Contains('*') || p.Contains('?'))
        {
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true);
        }
        return fileName.Equals(p, StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains(p, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 源详情
/// </summary>
public class SourceDetail
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public List<string> FilePatterns { get; set; } = new();
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime? LastIndexed { get; set; }

    /// <summary>是否为知识库连接器（只读视图，写操作需回到知识库管理）</summary>
    public bool IsConnector { get; set; }
    public string? KnowledgeBaseId { get; set; }
    public string? ConnectorId { get; set; }

    /// <summary>该路径已挂载的知识库（允许跨知识库挂载，同一知识库内唯一）</summary>
    public List<LinkedKnowledgeBaseInfo> LinkedKnowledgeBases { get; set; } = new();

    /// <summary>行来源：SourceManagement（config.Sources）/ KnowledgeBaseOnly（仅由知识库连接器投影）。</summary>
    public string Origin { get; set; } = "SourceManagement";
}

public class LinkedKnowledgeBaseInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConnectorId { get; set; } = "";
}

public class LinkSourceRequest
{
    public string? KnowledgeBaseId { get; set; }
}

/// <summary>
/// 文件详情
/// </summary>
public class FileDetail
{
    public string Path { get; set; } = "";
    public int ChunkCount { get; set; }
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>
/// 添加源请求
/// </summary>
public class AddSourceRequest
{
    public string? Path { get; set; }
    public string? Name { get; set; }
    public SourceType? Type { get; set; }
    public string[]? FilePatterns { get; set; }

    /// <summary>可选：同时挂载到的知识库；缺省时挂到第一个启用知识库</summary>
    public string? KnowledgeBaseId { get; set; }
}

/// <summary>
/// 更新源请求
/// </summary>
public class UpdateSourceRequest
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string[]? FilePatterns { get; set; }
}

/// <summary>
/// 切换请求
/// </summary>
public class ToggleRequest
{
    public bool Enabled { get; set; }
}

/// <summary>
/// 扫描结果
/// </summary>
public class ScanResult
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public Dictionary<string, int> Extensions { get; set; } = new();
    public List<FileItem> Files { get; set; } = new();
    public int TotalDiskFiles { get; set; }
    public Dictionary<string, int> DiskExtensions { get; set; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// 文件项
/// </summary>
public class FileItem
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>
/// 文件分段项
/// </summary>
public class FileChunkItem
{
    public int Index { get; set; }
    public string? HeadingPath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = "";
}

/// <summary>
/// 批量行范围查询请求
/// </summary>
public class FileLinesRequest
{
    public List<FileLinesItem> Items { get; set; } = new();
}

public class FileLinesItem
{
    public string Path { get; set; } = "";
    /// <summary>0 表示不限起始行</summary>
    public int StartLine { get; set; } = 0;
    /// <summary>0 表示不限结束行</summary>
    public int EndLine { get; set; } = 0;
}

/// <summary>
/// 批量行范围查询响应
/// </summary>
public class FileLinesResponse
{
    public List<FileLinesResult> Results { get; set; } = new();
}

public class FileLinesResult
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Source { get; set; }
    public LineRange? RequestedRange { get; set; }
    public List<FileChunkItem> Chunks { get; set; } = new();
    public string? Error { get; set; }
}

public class LineRange
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

/// <summary>
/// 批量文件信息查询请求
/// </summary>
public class FilesInfoRequest
{
    public List<string> Paths { get; set; } = new();
}

/// <summary>
/// 批量文件信息查询响应
/// </summary>
public class FilesInfoResponse
{
    public List<FileInfoResult> Results { get; set; } = new();
}

public class FileInfoResult
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Source { get; set; }
    public int TotalChunks { get; set; }
    public int TotalLines { get; set; }
    public List<FileSectionItem> Sections { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// 文件章节项（不含正文内容）
/// </summary>
public class FileSectionItem
{
    public int Index { get; set; }
    public string? HeadingPath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

