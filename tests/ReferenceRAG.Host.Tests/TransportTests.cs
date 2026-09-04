using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Transport;
using ReferenceRAG.Service.Middleware;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class TransportTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder=WebApplication.CreateBuilder();
        builder.Services.AddInProcessServer(listen:false);
        var app=builder.Build();
        app.Use(async(context,next)=>{context.Items["ApiKeyEnabled"]=true;context.Items["ApiKeyValue"]="test-key";await next();});
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapPost("/api/echo",async(HttpContext context)=>{
            var input=await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            return Results.Json(new{Value=input.GetProperty("value").GetInt32(),Query=context.Request.Query["q"].ToString()},statusCode:202);
        });
        app.MapPost("/api/chat/stream",async(HttpContext context)=>{
            context.Response.ContentType="text/event-stream";
            await context.Response.WriteAsync("data: {\"delta\":\"中文\"}\n\n");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("data: {\"type\":\"done\"}\n\n");
        });
        app.MapPost("/api/chat/wait",async(HttpContext context)=>{
            context.Response.ContentType="text/event-stream";
            await context.Response.WriteAsync("data: started\n\n");
            await Task.Delay(Timeout.Infinite,context.RequestAborted);
        });
        await app.StartAsync();return app;
    }
    [Theory]
    [InlineData(null,401)]
    [InlineData("wrong",403)]
    [InlineData("test-key",202)]
    public async Task InProcessRequestsPreserveAuthenticationBindingAndStatus(string? key,int expected)
    {
        await using var app=await StartAsync();
        var headers=new Dictionary<string,string>{{"Content-Type","application/json"}};
        if(key!=null)headers["X-API-Key"]=key;
        using var input=new MemoryStream(Encoding.UTF8.GetBytes("{\"value\":42}"));using var output=new MemoryStream();
        var response=new HttpResponseFeature();
        await app.Services.GetRequiredService<InProcessServer>().DispatchAsync("POST","/api/echo?q=hello",headers,input,output,response,CancellationToken.None);
        Assert.Equal(expected,response.StatusCode);
        if(expected==202){using var json=JsonDocument.Parse(output.ToArray());Assert.Equal(42,json.RootElement.GetProperty("value").GetInt32());Assert.Equal("hello",json.RootElement.GetProperty("query").GetString());}
        await app.StopAsync();
    }
    [Fact]
    public async Task IpcStreamsPreserveIncrementalUnicodeAndCompletion()
    {
        await using var app=await StartAsync();var messages=new ConcurrentQueue<string>();
        await using var ipc=new IpcDispatcher(app.Services.GetRequiredService<InProcessServer>(),messages.Enqueue,app.Services.GetRequiredService<ILogger<IpcDispatcher>>());
        await ipc.ReceiveAsync(JsonSerializer.Serialize(new{id="stream",type="request",method="POST",path="/api/chat/stream",headers=new Dictionary<string,string>{{"X-API-Key","test-key"}}}));
        var received=messages.Select(text=>JsonDocument.Parse(text).RootElement.Clone()).ToArray();
        Assert.Equal("stream-start",received[0].GetProperty("type").GetString());
        Assert.Equal("stream-end",received[^1].GetProperty("type").GetString());
        Assert.Contains("中文",string.Join("",received.Where(x=>x.GetProperty("type").GetString()=="stream-chunk").Select(x=>x.GetProperty("data").GetString())));
        await app.StopAsync();
    }
    [Fact]
    public async Task IpcCancellationStopsAnActiveStream()
    {
        await using var app=await StartAsync();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages=new ConcurrentQueue<string>();
        await using var ipc=new IpcDispatcher(app.Services.GetRequiredService<InProcessServer>(),text=>{
            messages.Enqueue(text);
            if(text.Contains("stream-chunk"))started.TrySetResult();
        },app.Services.GetRequiredService<ILogger<IpcDispatcher>>());
        var pending=ipc.ReceiveAsync(JsonSerializer.Serialize(new{id="cancel",type="request",method="POST",path="/api/chat/wait",headers=new Dictionary<string,string>{{"X-API-Key","test-key"}}}));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ipc.ReceiveAsync("{\"id\":\"cancel\",\"type\":\"cancel\"}");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(messages,text=>text.Contains("Operation cancelled"));
        Assert.DoesNotContain(messages,text=>text.Contains("stream-end"));
        await app.StopAsync();
    }
    [Fact]
    public void DataDirectoryAllowsOnlyOneWriter()
    {
        var path=Path.Combine(Path.GetTempPath(),"rag-lease-"+Guid.NewGuid());Directory.CreateDirectory(path);
        using(var first=new ReferenceRAG.Infrastructure.Features.Database.DataDirectoryLease())
        using(var second=new ReferenceRAG.Infrastructure.Features.Database.DataDirectoryLease())
        {first.Acquire(path);Assert.Throws<InvalidOperationException>(()=>second.Acquire(path));}
        File.Delete(Path.Combine(path,".referencerag-writer.lock"));Directory.Delete(path);
    }
}
