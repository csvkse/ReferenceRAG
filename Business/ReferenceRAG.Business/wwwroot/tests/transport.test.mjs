import test from 'node:test';
import assert from 'node:assert/strict';
import {createIpcTransport} from '../core/transport/ipc.js';

test('IPC preserves method/body/status and never falls back to HTTP', async () => {
  let listener, sent;
  const bridge = {addEventListener:(_,fn)=>listener=fn, removeEventListener(){},postMessage:value=>sent=JSON.parse(JSON.parse(value).data)};
  const transport=createIpcTransport(bridge);
  const pending=transport.fetch('/api/example',{method:'POST',body:'{"value":1}'});
  assert.equal(sent.method,'POST'); assert.equal(sent.body,'{"value":1}');
  listener({data:JSON.stringify({id:sent.id,type:'response',status:403,body:'{"error":"denied"}'})});
  const response=await pending;
  assert.equal(response.status,403); assert.equal((await response.json()).error,'denied');
  transport.dispose();
});
test('IPC streams can be consumed incrementally and cancelled', async () => {
  let listener; const sent=[];
  const bridge={addEventListener:(_,fn)=>listener=fn,removeEventListener(){},postMessage:v=>sent.push(JSON.parse(JSON.parse(v).data))};
  const transport=createIpcTransport(bridge), abort=new AbortController();
  const pending=transport.fetch('/api/chat/stream',{method:'POST',signal:abort.signal});
  const id=sent[0].id;
  listener({data:{id,type:'stream-start',status:200,headers:{'content-type':'text/event-stream'}}});
  const response=await pending, reader=response.body.getReader();
  listener({data:{id,type:'stream-chunk',data:'data: {"type":"delta"}\n\n'}});
  assert.match(new TextDecoder().decode((await reader.read()).value),/delta/);
  abort.abort();
  await assert.rejects(reader.read());
  assert.equal(sent.at(-1).type,'cancel');
  transport.dispose();
});
test('disposed and unavailable bridges reject outstanding requests', async () => {
  const transport=createIpcTransport({addEventListener(){},removeEventListener(){},postMessage(){}});
  const pending=transport.fetch('/api/test'); transport.dispose();
  await assert.rejects(pending,/disposed/i);
  const unavailable=createIpcTransport(null);
  await assert.rejects(unavailable.fetch('/api/test'),/unavailable/i);
});
