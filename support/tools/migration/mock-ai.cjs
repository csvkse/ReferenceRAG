// Deterministic, local-only AI fixture for migration integration checks.
const http=require('node:http');
const server=http.createServer(async(req,res)=>{
  let raw='';for await(const chunk of req)raw+=chunk;
  const body=raw?JSON.parse(raw):{};
  if(req.url==='/v1/embeddings') {
    const inputs=Array.isArray(body.input)?body.input:[body.input];
    res.setHeader('Content-Type','application/json');
    res.end(JSON.stringify({object:'list',data:inputs.map((text,index)=>({object:'embedding',index,embedding:[1,0.2,0.3,0.4]})),model:'migration-fixture',usage:{prompt_tokens:1,total_tokens:1}}));return;
  }
  if(req.url==='/v1/chat/completions') {
    res.setHeader('Content-Type','text/event-stream');
    for(const text of ['迁移','验证','成功']) res.write('data: '+JSON.stringify({id:'fixture',object:'chat.completion.chunk',created:1,model:'migration-fixture',choices:[{index:0,delta:{content:text},finish_reason:null}]})+'\n\n');
    res.end('data: '+JSON.stringify({id:'fixture',object:'chat.completion.chunk',created:1,model:'migration-fixture',choices:[{index:0,delta:{},finish_reason:'stop'}]})+'\n\ndata: [DONE]\n\n');return;
  }
  res.statusCode=404;res.end();
});
server.listen(17898,'127.0.0.1',()=>console.log('Local AI fixture listening on 17898'));
