using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 内容指纹检测服务 - 检测重复内容、移动/重命名
/// </summary>
public class ContentHashDetector
{
    private readonly IVectorStore _vectorStore;

    public ContentHashDetector(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// 计算内容指纹（SHA256）
    /// </summary>
    public string ComputeFingerprint(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 计算文件指纹
    /// </summary>
    public async Task<string> ComputeFileFingerprintAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        var content = await File.ReadAllTextAsync(filePath);
        return ComputeFingerprint(content);
    }

    /// <summary>
    /// 检测是否为重复内容
    /// </summary>
    public async Task<DuplicateCheckResult> CheckDuplicateAsync(string content)
    {
        var fingerprint = ComputeFingerprint(content);
        var existingFile = await _vectorStore.GetFileByHashAsync(fingerprint);

        return new DuplicateCheckResult
        {
            Fingerprint = fingerprint,
            IsDuplicate = existingFile != null,
            ExistingFile = existingFile
        };
    }

    /// <summary>
    /// 检测文件移动/重命名
    /// </summary>
    public async Task<MoveDetectionResult> DetectMoveOrRenameAsync(
        string oldPath,
        string oldFingerprint)
    {
        // 1. 检查原路径是否还存在
        if (File.Exists(oldPath))
        {
            return new MoveDetectionResult { IsMoved = false };
        }

        // 2. 查找具有相同指纹的文件
        var existingFile = await _vectorStore.GetFileByHashAsync(oldFingerprint);
        if (existingFile != null && existingFile.Path != oldPath)
        {
            return new MoveDetectionResult
            {
                IsMoved = true,
                OldPath = oldPath,
                NewPath = existingFile.Path,
                Fingerprint = oldFingerprint
            };
        }

        // 3. 没有找到匹配，可能是删除
        return new MoveDetectionResult
        {
            IsMoved = false,
            IsDeleted = true,
            OldPath = oldPath
        };
    }

    /// <summary>
    /// 批量检测内容变化
    /// </summary>
    public async Task<Dictionary<string, ContentChangeResult>> DetectChangesAsync(
        IEnumerable<string> filePaths)
    {
        var results = new Dictionary<string, ContentChangeResult>();

        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                results[filePath] = new ContentChangeResult
                {
                    Status = ContentStatus.Deleted
                };
                continue;
            }

            var currentFingerprint = await ComputeFileFingerprintAsync(filePath);
            var existingFile = await _vectorStore.GetFileByPathAsync(filePath);

            if (existingFile == null)
            {
                results[filePath] = new ContentChangeResult
                {
                    Status = ContentStatus.New,
                    Fingerprint = currentFingerprint
                };
            }
            else if (existingFile.ContentHash != currentFingerprint)
            {
                results[filePath] = new ContentChangeResult
                {
                    Status = ContentStatus.Modified,
                    Fingerprint = currentFingerprint,
                    OldFingerprint = existingFile.ContentHash
                };
            }
            else
            {
                results[filePath] = new ContentChangeResult
                {
                    Status = ContentStatus.Unchanged,
                    Fingerprint = currentFingerprint
                };
            }
        }

        return results;
    }

    /// <summary>
    /// 计算增量指纹（用于大文件快速检测）
    /// </summary>
    public async Task<string> ComputeIncrementalFingerprintAsync(
        string filePath, 
        int sampleSize = 4096)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        var fileInfo = new FileInfo(filePath);
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();

        // 采样策略：文件头 + 文件尾 + 文件大小
        var samples = new List<byte>();
        
        // 文件大小（8字节）
        var sizeBytes = BitConverter.GetBytes(fileInfo.Length);
        samples.AddRange(sizeBytes);

        // 文件头采样
        var headBuffer = new byte[Math.Min(sampleSize, fileInfo.Length)];
        await stream.ReadAsync(headBuffer, 0, headBuffer.Length);
        samples.AddRange(headBuffer);

        // 文件尾采样
        if (fileInfo.Length > sampleSize)
        {
            stream.Position = fileInfo.Length - sampleSize;
            var tailBuffer = new byte[sampleSize];
            await stream.ReadAsync(tailBuffer, 0, tailBuffer.Length);
            samples.AddRange(tailBuffer);
        }

        var hash = sha256.ComputeHash(samples.ToArray());
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// 重复检测结果
/// </summary>
public class DuplicateCheckResult
{
    public string Fingerprint { get; set; } = string.Empty;
    public bool IsDuplicate { get; set; }
    public FileRecord? ExistingFile { get; set; }
}

/// <summary>
/// 移动检测结果
/// </summary>
public class MoveDetectionResult
{
    public bool IsMoved { get; set; }
    public bool IsDeleted { get; set; }
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary>
/// 内容变化结果
/// </summary>
public class ContentChangeResult
{
    public ContentStatus Status { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string? OldFingerprint { get; set; }
}

/// <summary>
/// 内容状态
/// </summary>
public enum ContentStatus
{
    New,
    Modified,
    Deleted,
    Unchanged
}
