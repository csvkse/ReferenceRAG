using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.ApiHost.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ApiKeysController : ControllerBase
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly ConfigManager _configManager;
    private readonly ILogger<ApiKeysController> _logger;

    public ApiKeysController(
        IApiKeyStore apiKeyStore,
        ConfigManager configManager,
        ILogger<ApiKeysController> logger)
    {
        _apiKeyStore = apiKeyStore;
        _configManager = configManager;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        try
        {
            var keys = await _apiKeyStore.GetAllAsync();
            return Ok(keys.OrderByDescending(k => k.CreatedAt).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 API Key 列表失败");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"获取列表失败: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "密钥友好名称不能为空" });
        }

        try
        {
            var (entity, rawKey) = await _apiKeyStore.CreateAsync(
                name: request.Name,
                allowApi: request.AllowApi,
                allowMcp: request.AllowMcp,
                allowedScopes: request.AllowedScopes ?? "[\"*\"]",
                allowedMcpTools: request.AllowedMcpTools ?? "[\"*\"]",
                sourceScopeMode: request.SourceScopeMode ?? "all",
                allowedSources: request.AllowedSources ?? "[]",
                expiresAt: request.ExpiresAt
            );

            _logger.LogInformation("已创建新 API Key: {Name} ({Prefix}...) [ID: {Id}]", entity.Name, entity.KeyPrefix, entity.Id);
            return Ok(new CreateApiKeyResponse(true, rawKey, entity, "API Key 创建成功，请妥善保存明文密钥，该密钥仅显示一次！"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建 API Key 失败");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"创建密钥失败: {ex.Message}" });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "密钥友好名称不能为空" });
        }

        try
        {
            var success = await _apiKeyStore.UpdateAsync(
                id: id,
                name: request.Name,
                allowApi: request.AllowApi,
                allowMcp: request.AllowMcp,
                allowedScopes: request.AllowedScopes ?? "[\"*\"]",
                allowedMcpTools: request.AllowedMcpTools ?? "[\"*\"]",
                sourceScopeMode: request.SourceScopeMode ?? "all",
                allowedSources: request.AllowedSources ?? "[]",
                expiresAt: request.ExpiresAt
            );

            if (!success)
            {
                return NotFound(new { error = "未找到指定的 API Key" });
            }

            var updated = await _apiKeyStore.GetByIdAsync(id);
            return Ok(new { success = true, entity = updated, message = "权限与配置已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 API Key 失败: {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"更新失败: {ex.Message}" });
        }
    }

    [HttpPatch("{id}/toggle")]
    public async Task<IActionResult> Toggle(string id, [FromBody] ToggleApiKeyRequest request)
    {
        try
        {
            var success = await _apiKeyStore.ToggleAsync(id, request.IsEnabled);
            if (!success)
            {
                return NotFound(new { error = "未找到指定的 API Key" });
            }

            return Ok(new { success = true, id, isEnabled = request.IsEnabled, message = request.IsEnabled ? "密钥已启用" : "密钥已停用" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换 API Key 状态失败: {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"操作失败: {ex.Message}" });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            var success = await _apiKeyStore.DeleteAsync(id);
            if (!success)
            {
                return NotFound(new { error = "未找到指定的 API Key" });
            }

            _logger.LogInformation("已彻底吊销删除 API Key [ID: {Id}]", id);
            return Ok(new { success = true, id, message = "API Key 已被彻底删除" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 API Key 失败: {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"删除失败: {ex.Message}" });
        }
    }

    [HttpGet("meta-catalog")]
    public IActionResult GetMetaCatalog()
    {
        try
        {
            var config = _configManager.Load();
            var availableSources = config.Sources
                .Select(s => new { name = s.Name, path = s.Path, enabled = s.Enabled })
                .ToList();

            var categories = McpCatalogRegistry.GetCatalog();

            // 保持向后兼容现有前端/接口调用方字段
            var modules = categories.Select(c => new
            {
                key = c.ModuleCode,
                label = c.Name,
                description = c.Description,
                supportsApi = c.SupportsApi,
                supportsMcp = c.SupportsMcp,
                toolCount = c.Tools.Count
            }).ToList();

            var mcpTools = categories.SelectMany(c => c.Tools).Select(t => new
            {
                name = t.Name,
                description = t.Description,
                riskLevel = t.RiskLevel,
                module = t.ModuleCode
            }).ToList();

            return Ok(new
            {
                categories, // 统一按功能模块划分的树状目录（对齐 WebToolForLinux）
                modules,    // 兼容字段
                mcpTools,   // 兼容字段
                sources = availableSources
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取权限元数据目录失败");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"获取权限元数据失败: {ex.Message}" });
        }
    }
}

public sealed record CreateApiKeyRequest(
    string Name,
    bool AllowApi = true,
    bool AllowMcp = true,
    string? AllowedScopes = "[\"*\"]",
    string? AllowedMcpTools = "[\"*\"]",
    string? SourceScopeMode = "all",
    string? AllowedSources = "[]",
    DateTime? ExpiresAt = null
);

public sealed record CreateApiKeyResponse(
    bool Success,
    string RawKey,
    ApiKeyEntity Entity,
    string Message
);

public sealed record UpdateApiKeyRequest(
    string Name,
    bool AllowApi,
    bool AllowMcp,
    string? AllowedScopes,
    string? AllowedMcpTools,
    string? SourceScopeMode,
    string? AllowedSources,
    DateTime? ExpiresAt = null
);

public sealed record ToggleApiKeyRequest(
    bool IsEnabled
);
