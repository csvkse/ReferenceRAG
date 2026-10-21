import './core/transport/index.js';
import {createApp} from 'vue';
import {createPinia} from './core/state.js';
import * as naive from 'naive-ui';
import * as icons from './vendor/icons.js';

export function showRuntimeError(title, message) {
  document.getElementById('runtime-error')?.remove();
  const notice = document.createElement('div');
  notice.id = 'runtime-error';
  notice.setAttribute('role', 'alert');
  notice.style.cssText = 'position:fixed;top:20px;right:20px;max-width:500px;z-index:999999;background:#1f1315;border:1px solid #ff4d4f;border-radius:8px;padding:16px 20px;color:#fff;box-shadow:0 8px 32px rgba(0,0,0,0.6);font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;line-height:1.5;';
  notice.innerHTML = `
    <div style="display:flex;align-items:flex-start;justify-content:space-between;gap:12px">
      <div style="font-weight:600;font-size:15px;color:#ff7875;display:flex;align-items:center;gap:6px">
        <span>⚠️</span><span>${title || '界面操作失败'}</span>
      </div>
      <button id="runtime-error-close" style="background:none;border:none;color:#888;cursor:pointer;font-size:18px;line-height:1;padding:0">&times;</button>
    </div>
    <div style="margin-top:8px;font-size:13px;color:#ddd;word-break:break-word;font-family:monospace;background:rgba(0,0,0,0.3);padding:8px 10px;border-radius:4px;max-height:160px;overflow-y:auto">
      ${(message || '未知错误').replace(/</g, '&lt;').replace(/>/g, '&gt;')}
    </div>
    <div style="margin-top:12px;display:flex;justify-content:flex-end;gap:10px">
      <button id="runtime-error-reload" style="background:#ff4d4f;border:none;color:#fff;padding:6px 14px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:500">刷新重试</button>
    </div>
  `;
  document.body.append(notice);
  notice.querySelector('#runtime-error-close')?.addEventListener('click', () => notice.remove());
  notice.querySelector('#runtime-error-reload')?.addEventListener('click', () => window.location.reload());
}

try {
  const [{default:App},{default:router}]=await Promise.all([import('./app/App.js'),import('./app/router/index.js')]);
  const app=createApp(App);
  for(const [name,component] of Object.entries(icons)) app.component(name,component);
  for(const [name,component] of Object.entries(naive)) if(/^N[A-Z]/.test(name) && component?.name) app.component(name,component);
  app.config.errorHandler=(error)=>{console.error('[Vue Runtime Error]', error); showRuntimeError('组件渲染或操作异常', error?.message || String(error));};
  app.use(createPinia()); app.use(router);
  await router.isReady(); app.mount('#app');
} catch(error) {
  document.getElementById('app').textContent='启动失败：'+error.message;
  console.error(error);
  showRuntimeError('应用初始化失败', error?.message || String(error));
}
