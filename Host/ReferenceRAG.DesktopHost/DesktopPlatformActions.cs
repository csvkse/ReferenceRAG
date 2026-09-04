using System.Threading;
using System.Threading.Tasks;
using InfiniFrame;
using ReferenceRAG.Business.Features.App.Contracts;

namespace ReferenceRAG.DesktopHost;

internal sealed class DesktopPlatformActions : IPlatformActions
{
    public IInfiniFrameWindow? Window {get;set;}
    public bool CanSelectFolder=>Window!=null;
    public async Task<string?> SelectFolderAsync(CancellationToken token)
    {
        if(Window==null)return null;
        var paths=await Window.ShowOpenFolderAsync("选择知识库目录","",false,token);
        return paths.Length>0?paths[0]:null;
    }
}
