using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace ReferenceRAG.ApiHost.Transport;

/// <summary>One dispatcher per trusted window. No HTTP fallback and no arbitrary external URLs.</summary>
public sealed class IpcDispatcher(InProcessServer server, Action<string> send, ILogger<IpcDispatcher> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
    private readonly ConcurrentDictionary<string, Task> _running = new();
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private bool _disposed;
    private void Send(object value) { if(!_disposed) send(JsonSerializer.Serialize(value,Options)); }

    public Task ReceiveAsync(string json)
    {
        if(_disposed) return Task.CompletedTask;
        IpcRequest? message;
        try { message=JsonSerializer.Deserialize<IpcRequest>(json,Options); }
        catch(JsonException) { return Task.CompletedTask; }
        if(message is null || string.IsNullOrWhiteSpace(message.Id) || message.Id.Length>128) return Task.CompletedTask;
        if(message.Type=="cancel") {
            if(_requests.TryGetValue(message.Id,out var existing)) {
                try { existing.Cancel(); } catch(ObjectDisposedException) { /* Request already completed. */ }
            }
            return Task.CompletedTask;
        }
        if(message.Type!="request") return Task.CompletedTask;
        if(_requests.Count>=64) { Send(new {id=message.Id,type="error",error="Too many pending requests"}); return Task.CompletedTask; }
        var cts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        if(!_requests.TryAdd(message.Id,cts)) {cts.Dispose();return Task.CompletedTask;}
        var task=DispatchAsync(message,cts);
        _running[message.Id]=task;
        _=task.ContinueWith(_=>_running.TryRemove(message.Id,out var ignored),TaskScheduler.Default);
        return task;
    }

    private async Task DispatchAsync(IpcRequest request,CancellationTokenSource cts)
    {
        try {
            if(request.Path is null || (!request.Path.StartsWith("/api/",StringComparison.OrdinalIgnoreCase)
                && !request.Path.StartsWith("/swagger/",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Unsupported IPC route");
            if(request.Path.Contains("..",StringComparison.Ordinal) || request.Body?.Length>8*1024*1024) throw new ArgumentException("Invalid IPC request");
            if(!new[]{"GET","POST","PUT","PATCH","DELETE","OPTIONS"}.Contains(request.Method)) throw new ArgumentException("Unsupported method");
            using var input=new MemoryStream(Encoding.UTF8.GetBytes(request.Body??""));
            var response=new HttpResponseFeature();
            using var output=new IpcResponseStream(response,value=>Send(value),request.Id);
            await server.DispatchAsync(request.Method,request.Path,request.Headers??new Dictionary<string,string>(),input,output,response,cts.Token);
            output.Complete();
        } catch(OperationCanceledException) {Send(new{id=request.Id,type="error",error="Operation cancelled"});}
        catch(Exception ex) {logger.LogError(ex,"IPC request failed: {Path}",request.Path);Send(new{id=request.Id,type="error",error="操作失败，请查看日志"});}
        finally {_requests.TryRemove(request.Id,out _);cts.Dispose();}
    }
    public void Publish(string name,object payload) => Send(new{type="event",name,body=payload});
    public async ValueTask DisposeAsync()
    {
        _disposed=true;_lifetime.Cancel();
        await Task.WhenAll(_running.Values);_lifetime.Dispose();
    }
    private sealed record IpcRequest(string Id,string Type,string Method,string? Path,Dictionary<string,string>? Headers,string? Body);

    private sealed class IpcResponseStream(HttpResponseFeature response,Action<object> send,string id) : Stream
    {
        private readonly MemoryStream _buffer=new();
        private readonly Decoder _decoder=Encoding.UTF8.GetDecoder();
        private bool _started;
        private bool Streaming => response.Headers.ContentType.ToString().StartsWith("text/event-stream",StringComparison.OrdinalIgnoreCase);
        public override void Write(byte[] buffer,int offset,int count) => Write(buffer.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if(Streaming) {
                if(!_started) {send(new{id,type="stream-start",status=response.StatusCode,headers=Headers()});_started=true;}
                var chars=new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
                var count=_decoder.GetChars(buffer,chars,false);
                if(count>0) send(new{id,type="stream-chunk",data=new string(chars,0,count)});
            } else {
                if(_buffer.Length+buffer.Length>32*1024*1024) throw new InvalidOperationException("IPC response exceeds 32 MiB");
                _buffer.Write(buffer);
            }
        }
        private Dictionary<string,string> Headers()=>response.Headers.ToDictionary(p=>p.Key,p=>p.Value.ToString());
        public void Complete()
        {
            if(Streaming) {
                if(!_started) {send(new{id,type="stream-start",status=response.StatusCode,headers=Headers()});_started=true;}
                var tail=new char[4];var count=_decoder.GetChars(ReadOnlySpan<byte>.Empty,tail,true);
                if(count>0) send(new{id,type="stream-chunk",data=new string(tail,0,count)});
                send(new{id,type="stream-end"});
            } else send(new{id,type="response",status=response.StatusCode,headers=Headers(),body=Encoding.UTF8.GetString(_buffer.ToArray())});
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default) {cancellationToken.ThrowIfCancellationRequested();Write(buffer.Span);return ValueTask.CompletedTask;}
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken) {cancellationToken.ThrowIfCancellationRequested();Write(buffer,offset,count);return Task.CompletedTask;}
        public override void Flush(){}
        public override Task FlushAsync(CancellationToken token)=>Task.CompletedTask;
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)_buffer.Dispose();base.Dispose(disposing);}
    }
}
