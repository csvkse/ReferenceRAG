using ReferenceRAG.Core.Services;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Tests;

public class ContentHashDetectorTests
{
    private readonly ContentHashDetector _detector;
    private readonly Mock<IVectorStore> _mockVectorStore;

    public ContentHashDetectorTests()
    {
        _mockVectorStore = new Mock<IVectorStore>();
        _detector = new ContentHashDetector(_mockVectorStore.Object);
    }

    [Fact]
    public void ComputeFingerprint_WithSameContent_ReturnsSameHash()
    {
        var content = "Test content";
        var hash1 = _detector.ComputeFingerprint(content);
        var hash2 = _detector.ComputeFingerprint(content);
        
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeFingerprint_WithDifferentContent_ReturnsDifferentHash()
    {
        var hash1 = _detector.ComputeFingerprint("Content A");
        var hash2 = _detector.ComputeFingerprint("Content B");
        
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeFingerprint_WithEmptyContent_ReturnsValidHash()
    {
        var hash = _detector.ComputeFingerprint("");
        
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA256 produces 64 hex characters
    }

    [Fact]
    public void ComputeFingerprint_WithUnicodeContent_ReturnsValidHash()
    {
        var hash = _detector.ComputeFingerprint("你好世界 🌍");
        
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public async Task CheckDuplicateAsync_WhenNotDuplicate_ReturnsFalse()
    {
        _mockVectorStore.Setup(x => x.GetFileByHashAsync(It.IsAny<string>(), default))
            .ReturnsAsync((FileRecord?)null);

        var result = await _detector.CheckDuplicateAsync("Unique content");

        Assert.False(result.IsDuplicate);
        Assert.Null(result.ExistingFile);
    }

    [Fact]
    public async Task CheckDuplicateAsync_WhenDuplicate_ReturnsTrue()
    {
        var existingFile = new FileRecord { Id = "file-1", Path = "/test.md" };
        _mockVectorStore.Setup(x => x.GetFileByHashAsync(It.IsAny<string>(), default))
            .ReturnsAsync(existingFile);

        var result = await _detector.CheckDuplicateAsync("Duplicate content");

        Assert.True(result.IsDuplicate);
        Assert.NotNull(result.ExistingFile);
        Assert.Equal("file-1", result.ExistingFile.Id);
    }

    [Fact]
    public async Task CheckDuplicateAsync_ReturnsCorrectFingerprint()
    {
        _mockVectorStore.Setup(x => x.GetFileByHashAsync(It.IsAny<string>(), default))
            .ReturnsAsync((FileRecord?)null);

        var content = "Test content for fingerprint";
        var result = await _detector.CheckDuplicateAsync(content);

        Assert.Equal(_detector.ComputeFingerprint(content), result.Fingerprint);
    }

    [Fact]
    public void ComputeFingerprint_IsDeterministic()
    {
        var content = "This is a test message with special chars: !@#$%^&*()";

        for (int i = 0; i < 10; i++)
        {
            var hash = _detector.ComputeFingerprint(content);
            Assert.Equal(hash, _detector.ComputeFingerprint(content));
        }
    }
}
