export function createIpcTransport(bridge, {timeoutMs = 60000} = {}) {
  const pending = new Map(), listeners = new Set();
  let disposed = false;
  function post(message) {
    if (disposed) throw new Error('IPC transport disposed');
    if (!bridge?.postMessage) throw new Error('WebView IPC bridge unavailable');
    bridge.postMessage(JSON.stringify({id:'ipc-msg',command:'Post',version:2,data:JSON.stringify(message)}));
  }
  function finish(id) {
    const item=pending.get(id); if(!item) return;
    clearTimeout(item.timer); item.signal?.removeEventListener('abort',item.abort); pending.delete(id); return item;
  }
  function fail(id,error) {
    const item=finish(id); if(!item) return;
    item.controller?.error(error); item.reject(error);
  }
  function receive(event) {
    let message;
    try { message=typeof event.data==='string'?JSON.parse(event.data):event.data; } catch { return; }
    if(message?.type==='event') { for(const fn of listeners) fn(message.name,message.body); return; }
    const item=pending.get(message?.id); if(!item) return;
    clearTimeout(item.timer);
    item.timer=setTimeout(()=>item.abort(new Error('IPC response timed out')),timeoutMs);
    if(message.type==='stream-start') {
      const stream=new ReadableStream({start:c=>item.controller=c,cancel:()=>item.abort()});
      item.resolve(new Response(stream,{status:message.status,headers:message.headers}));
    } else if(message.type==='stream-chunk') {
      if(!item.controller) return fail(message.id,new Error('Stream was not started'));
      if(item.controller.desiredSize < -1024) return item.abort(new Error('Stream consumer too slow'));
      item.controller.enqueue(new TextEncoder().encode(message.data));
    } else if(message.type==='stream-end') {
      finish(message.id)?.controller?.close();
    } else if(message.type==='error') {
      fail(message.id,new Error(message.error || 'IPC operation failed'));
    } else if(message.type==='response') {
      if(!Number.isInteger(message.status) || message.status<200 || message.status>599) return fail(message.id,new Error('Invalid IPC response status'));
      finish(message.id);
      item.resolve(new Response([204,205,304].includes(message.status)?null:(message.body??''),{status:message.status,headers:message.headers}));
    }
  }
  bridge?.addEventListener?.('message',receive);
  return {
    fetch(path,options={}) {
      return new Promise((resolve,reject)=>{
        if(options.signal?.aborted) return reject(new DOMException('Aborted','AbortError'));
        const id=crypto.randomUUID();
        const item={resolve,reject,signal:options.signal};
        item.abort=(reason)=>{
          try { post({id,type:'cancel'}); } catch {}
          fail(id,reason instanceof Error?reason:new DOMException('Aborted','AbortError'));
        };
        item.timer=setTimeout(()=>item.abort(new Error('IPC request timed out')),timeoutMs);
        pending.set(id,item); options.signal?.addEventListener('abort',item.abort,{once:true});
        try { post({id,type:'request',method:options.method||'GET',path,headers:Object.fromEntries(new Headers(options.headers)),body:options.body??null}); }
        catch(error) { fail(id,error); }
      });
    },
    subscribe(callback) { listeners.add(callback); return ()=>listeners.delete(callback); },
    dispose() {
      for(const item of [...pending.values()]) item.abort(new Error('IPC transport disposed'));
      disposed=true; bridge?.removeEventListener?.('message',receive); listeners.clear();
    }
  };
}
