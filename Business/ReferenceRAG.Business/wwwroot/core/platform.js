import {ragFetch,isDesktop} from './transport/index.js';
export {isDesktop};
export async function selectFolder() {
  const response=await ragFetch('/api/platform/select-folder',{method:'POST',headers:{'X-API-Key':localStorage.getItem('reference_rag_api_key')||''}});
  if(!response.ok)throw new Error('目录选择失败');
  const data=await response.json();return data.Path??data.path;
}
