import {ipc,isDesktop} from './index.js';
import {HubConnectionBuilder} from '@microsoft/signalr';
export function createIndexConnection() {
  if(!isDesktop) return new HubConnectionBuilder().withUrl('/hubs/index',{withCredentials:false,headers:{'X-API-Key':localStorage.getItem('reference_rag_api_key')||''}}).withAutomaticReconnect().build();
  const handlers=new Map(); let remove;
  return {
    on(name,callback){handlers.set(name,callback);},
    onreconnected(){},
    async start(){remove=ipc.subscribe((name,payload)=>handlers.get(name)?.(payload));},
    async stop(){remove?.();handlers.clear();},
    async invoke(name){if(!['JoinIndexGroup','LeaveIndexGroup'].includes(name)) throw new Error('Unknown subscription');}
  };
}
