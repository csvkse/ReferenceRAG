using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ReferenceRAG.ApiHost.Transport;

/// <summary>Runs the same authenticated MVC pipeline for Web HTTP and desktop IPC, without loopback HTTP.</summary>
public sealed class InProcessServer(IServer networkServer, bool listen) : IServer
{
    private Func<IFeatureCollection,Task>? _dispatch;
    public IFeatureCollection Features => networkServer.Features;
    public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken token) where TContext : notnull
    {
        _dispatch = async features => {
            var context = application.CreateContext(features);
            Exception? error = null;
            try { await application.ProcessRequestAsync(context); }
            catch (Exception ex) { error = ex; throw; }
            finally { application.DisposeContext(context, error); }
        };
        if(listen) await networkServer.StartAsync(application,token);
    }
    public Task StopAsync(CancellationToken token) { _dispatch=null; return listen?networkServer.StopAsync(token):Task.CompletedTask; }
    public void Dispose() => networkServer.Dispose();

    public async Task DispatchAsync(string method, string path, IReadOnlyDictionary<string,string> headers,
        Stream input, Stream output, HttpResponseFeature response, CancellationToken token)
    {
        var dispatch=_dispatch ?? throw new InvalidOperationException("Application has not started.");
        if(!path.StartsWith('/') || path.StartsWith("//") || path.Contains('#')) throw new ArgumentException("Only local API paths are allowed.");
        var parts=path.Split('?',2);
        var request=new HttpRequestFeature { Method=method,Scheme="http",Protocol="HTTP/1.1",Path=parts[0],QueryString=parts.Length==2?"?"+parts[1]:"",Body=input,RawTarget=path };
        request.Headers.Host="localhost";
        foreach(var (key,value) in headers) if(!key.Equals("Host",StringComparison.OrdinalIgnoreCase)) request.Headers[key]=value;
        if(input.CanSeek) request.Headers.ContentLength=input.Length;
        var features=new FeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(output));
        features.Set<IHttpRequestLifetimeFeature>(new RequestLifetime(token));
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature {RemoteIpAddress=IPAddress.Loopback,LocalIpAddress=IPAddress.Loopback});
        features.Set<IHttpBodyControlFeature>(new BodyControl());
        features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(input.CanSeek && input.Length>0));
        await dispatch(features);
    }
    private sealed class RequestLifetime(CancellationToken token) : IHttpRequestLifetimeFeature
    { public CancellationToken RequestAborted {get;set;}=token; public void Abort() {} }
    private sealed class BodyControl : IHttpBodyControlFeature {public bool AllowSynchronousIO {get;set;}=true;}
    private sealed class BodyDetection(bool hasBody) : IHttpRequestBodyDetectionFeature {public bool CanHaveBody=>hasBody;}
}

public static class InProcessServerExtensions
{
    public static void AddInProcessServer(this IServiceCollection services,bool listen)
    {
        var original=services.Last(d=>d.ServiceType==typeof(IServer));
        services.RemoveAll<IServer>();
        services.AddSingleton(sp=>new InProcessServer((IServer)(original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp,original.ImplementationType!)),listen));
        services.AddSingleton<IServer>(sp=>sp.GetRequiredService<InProcessServer>());
        services.AddTransient<LocalApiHandler>();
        services.AddHttpClient("Rag.LocalApi").ConfigurePrimaryHttpMessageHandler<LocalApiHandler>();
    }
}

public sealed class LocalApiHandler(InProcessServer server,IConfiguration configuration, ReferenceRAG.Core.Services.ConfigManager configManager) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var headers=request.Headers.ToDictionary(h=>h.Key,h=>string.Join(",",h.Value));
        if(request.Content!=null) foreach(var h in request.Content.Headers) headers[h.Key]=string.Join(",",h.Value);
        var key=configManager.Load().Service?.ApiKey;
        if(string.IsNullOrEmpty(key) && configuration.GetValue<bool>("ApiKey:Enabled")) key=configuration["ApiKey:Key"];
        if(!string.IsNullOrEmpty(key)) headers["X-API-Key"]=key;
        using var input=new MemoryStream(request.Content==null?[]:await request.Content.ReadAsByteArrayAsync(token));
        using var output=new MemoryStream();
        var response=new HttpResponseFeature();
        await server.DispatchAsync(request.Method.Method,request.RequestUri!.PathAndQuery,headers,input,output,response,token);
        return new HttpResponseMessage((HttpStatusCode)response.StatusCode){Content=new ByteArrayContent(output.ToArray()),RequestMessage=request};
    }
}
