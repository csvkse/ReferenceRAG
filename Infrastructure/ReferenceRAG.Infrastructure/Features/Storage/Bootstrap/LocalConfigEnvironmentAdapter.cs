namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System;
using System.IO;
using System.Text;

/// <summary>
/// 本地物理文件系统环境适配器
/// </summary>
public class LocalConfigEnvironmentAdapter : IConfigEnvironmentAdapter
{
    private readonly string _baseDirectory;
    private readonly string _configPath;
    private readonly string _templatePath;

    public string BaseDirectory => _baseDirectory;
    public string ConfigPath => _configPath;
    public string TemplatePath => _templatePath;

    public LocalConfigEnvironmentAdapter(string? baseDirectory = null, string? customConfigPath = null, string? customTemplatePath = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? (Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? Directory.GetCurrentDirectory())
            : Path.GetFullPath(baseDirectory);

        _configPath = string.IsNullOrWhiteSpace(customConfigPath)
            ? Path.Combine(_baseDirectory, "appsettings.json")
            : Path.GetFullPath(customConfigPath);

        if (!string.IsNullOrWhiteSpace(customTemplatePath))
        {
            _templatePath = Path.GetFullPath(customTemplatePath);
        }
        else
        {
            // 默认优先查找同目录下的 appsettings.template.json，其次查找 support/config/appsettings.template.json
            var localTemplate = Path.Combine(_baseDirectory, "appsettings.template.json");
            if (File.Exists(localTemplate))
            {
                _templatePath = localTemplate;
            }
            else
            {
                var fallbackTemplate = Path.Combine(_baseDirectory, "support", "config", "appsettings.template.json");
                _templatePath = File.Exists(fallbackTemplate) ? fallbackTemplate : localTemplate;
            }
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = $"{path}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 忽略临时文件清理异常
            }
        }
    }

    public void Move(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        File.Move(sourcePath, destinationPath, overwrite);
    }

    public long GetFileSize(string path)
    {
        if (!File.Exists(path)) return 0;
        return new FileInfo(path).Length;
    }

    public bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var testFile = Path.Combine(directoryPath, $".perm_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
