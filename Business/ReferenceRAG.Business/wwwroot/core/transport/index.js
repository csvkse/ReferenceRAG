import axios from '../../vendor/axios.js';
import {createIpcTransport} from './ipc.js';
export const isDesktop=globalThis.location?.protocol==='app:';
const nativeFetch=globalThis.fetch.bind(globalThis);
export const ipc=isDesktop?createIpcTransport(globalThis.chrome?.webview):null;

export function ragFetch(input,options={}) {
  const url=new URL(input,location.href);
  const local=url.protocol===location.protocol && url.host===location.host;
  if(isDesktop && local) return ipc.fetch(url.pathname+url.search,options);
  return nativeFetch(input,options);
}

// Install before loading pages: every axios instance uses the selected transport.
axios.defaults.adapter=async config=>{
  const abort=new AbortController();
  const cancel=()=>abort.abort();
  config.signal?.addEventListener('abort',cancel,{once:true});
  if(config.signal?.aborted) abort.abort();
  const timer=config.timeout?setTimeout(cancel,config.timeout):null;
  try {
    const headers=Object.fromEntries(Object.entries(config.headers.toJSON()).filter(([,v])=>v!=null));
    const key=localStorage.getItem('reference_rag_api_key');
    const url=axios.getUri(config), target=new URL(url,location.href);
    if(key && target.host===location.host) headers['X-API-Key']=key;
    const res=await ragFetch(url,{method:config.method.toUpperCase(),headers,body:config.data,signal:abort.signal});
    const response={data:config.responseType==='blob'?await res.blob():await res.text(),status:res.status,statusText:res.statusText,headers:Object.fromEntries(res.headers),config,request:null};
    if(!config.validateStatus || config.validateStatus(res.status)) return response;
    throw new axios.AxiosError(`Request failed with status code ${res.status}`,'ERR_BAD_RESPONSE',config,null,response);
  } finally { clearTimeout(timer); config.signal?.removeEventListener('abort',cancel); }
};
globalThis.addEventListener?.('pagehide',()=>ipc?.dispose(),{once:true});
