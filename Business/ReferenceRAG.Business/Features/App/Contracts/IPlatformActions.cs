namespace ReferenceRAG.Business.Features.App.Contracts;

public interface IPlatformActions
{
    bool CanSelectFolder { get; }
    Task<string?> SelectFolderAsync(CancellationToken cancellationToken);
}
public sealed class BrowserPlatformActions : IPlatformActions
{
    public bool CanSelectFolder => false;
    public Task<string?> SelectFolderAsync(CancellationToken cancellationToken) => throw new NotSupportedException("浏览器端使用服务器目录，请输入服务器上的路径。");
}
