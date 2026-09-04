namespace ReferenceRAG.Infrastructure.Features.Database;

/// <summary>Prevents Web and Desktop from running independent writers against the same index directory.</summary>
public sealed class DataDirectoryLease : IDisposable
{
    private FileStream? _lease;
    public void Acquire(string dataPath)
    {
        if(_lease!=null)return;
        var directory=Path.GetFullPath(dataPath);Directory.CreateDirectory(directory);
        try {_lease=new FileStream(Path.Combine(directory,".referencerag-writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException ex){throw new InvalidOperationException("此数据目录已被另一个 ReferenceRAG 宿主使用，请关闭另一宿主或配置独立数据目录。",ex);}
    }
    public void Dispose(){_lease?.Dispose();_lease=null;}
}
