namespace ReferenceRAG.Host.Tests;

using System;
using System.IO;
using ReferenceRAG.Infrastructure.Features.Database;
using Xunit;

public class DataDirectoryLeaseTests : IDisposable
{
    private readonly string _testDir;

    public DataDirectoryLeaseTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "rag-lease-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                var lockFile = Path.Combine(_testDir, ".referencerag-writer.lock");
                if (File.Exists(lockFile))
                {
                    File.SetAttributes(lockFile, FileAttributes.Normal);
                    File.Delete(lockFile);
                }
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }

    [Fact]
    public void Acquire_AllowsOnlyOneWriter_ThrowsInvalidOperationExceptionOnConflict()
    {
        using var first = new DataDirectoryLease();
        using var second = new DataDirectoryLease();

        first.Acquire(_testDir);

        var ex = Assert.Throws<InvalidOperationException>(() => second.Acquire(_testDir));
        Assert.Contains("此数据目录已被另一个 ReferenceRAG 宿主使用", ex.Message);
    }

    [Fact]
    public void Acquire_WhenLockFileIsReadOnly_ThrowsUnauthorizedAccessExceptionWithActionableGuidance()
    {
        var lockFilePath = Path.Combine(_testDir, ".referencerag-writer.lock");
        File.WriteAllText(lockFilePath, "test");
        File.SetAttributes(lockFilePath, FileAttributes.ReadOnly);

        using var lease = new DataDirectoryLease();

        var ex = Assert.Throws<UnauthorizedAccessException>(() => lease.Acquire(_testDir));
        Assert.Contains("缺少写权限", ex.Message);
        Assert.Contains("chown -R 1000:1000", ex.Message);
        Assert.Contains("PUID=0", ex.Message);
    }

    [Fact]
    public void Dispose_ReleasesLockFile_AllowsSubsequentAcquire()
    {
        var lease1 = new DataDirectoryLease();
        lease1.Acquire(_testDir);
        lease1.Dispose();

        using var lease2 = new DataDirectoryLease();
        lease2.Acquire(_testDir); // Should succeed without exception
    }
}
