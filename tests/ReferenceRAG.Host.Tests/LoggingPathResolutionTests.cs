namespace ReferenceRAG.Host.Tests;

using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using WebApiWindowsService;
using Xunit;

public class LoggingPathResolutionTests : IDisposable
{
    private readonly string _tempDir;

    public LoggingPathResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup failure
        }
    }

    [Fact]
    public void ConfigureLogging_WhenLogsPathConfigured_UsesConfiguredPathWithoutCrashing()
    {
        var customLogDir = Path.Combine(_tempDir, "custom-logs");
        Directory.CreateDirectory(customLogDir);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Work:Directory"] = _tempDir;
        builder.Configuration["LogsPath"] = customLogDir;

        ServiceManager.ConfigureLogging(builder);

        Assert.True(Directory.Exists(customLogDir));
    }

    [Fact]
    public void ConfigureLogging_WhenLowerLogsExists_PrefersLowerLogsDirectory()
    {
        var lowerLogsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(lowerLogsDir);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Work:Directory"] = _tempDir;

        ServiceManager.ConfigureLogging(builder);

        Assert.True(Directory.Exists(lowerLogsDir));
    }
}
