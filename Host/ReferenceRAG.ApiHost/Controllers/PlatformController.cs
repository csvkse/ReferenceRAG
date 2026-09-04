using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Business.Features.App.Contracts;

namespace ReferenceRAG.ApiHost.Controllers;

[ApiController,Route("api/platform")]
public sealed class PlatformController(IPlatformActions platform) : ControllerBase
{
    [HttpGet("capabilities")]
    public object Capabilities()=>new { platform.CanSelectFolder };
    [HttpPost("select-folder")]
    public async Task<IActionResult> SelectFolder(CancellationToken token)
    {
        if(!platform.CanSelectFolder)return StatusCode(501,new{error="请填写服务器目录"});
        return Ok(new{Path=await platform.SelectFolderAsync(token)});
    }
}
