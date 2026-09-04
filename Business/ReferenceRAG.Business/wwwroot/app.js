import './core/transport/index.js';
import {createApp} from 'vue';
import {createPinia} from './core/state.js';
import * as naive from 'naive-ui';
import * as icons from './vendor/icons.js';
try {
  const [{default:App},{default:router}]=await Promise.all([import('./app/App.js'),import('./app/router/index.js')]);
  const app=createApp(App);
  for(const [name,component] of Object.entries(icons)) app.component(name,component);
  for(const [name,component] of Object.entries(naive)) if(/^N[A-Z]/.test(name) && component?.name) app.component(name,component);
  app.config.errorHandler=(error)=>{console.error(error); document.getElementById('runtime-error')?.remove(); const notice=document.createElement('div');notice.id='runtime-error';notice.setAttribute('role','alert');notice.textContent='界面操作失败：'+error.message;document.body.append(notice);};
  app.use(createPinia()); app.use(router);
  await router.isReady(); app.mount('#app');
} catch(error) { document.getElementById('app').textContent='启动失败：'+error.message;console.error(error); }
