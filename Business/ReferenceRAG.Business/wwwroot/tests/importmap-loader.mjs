// node --test 不认浏览器 importmap：注册 resolve 钩子，让 bare specifier
// （vue/axios/marked/naive-ui/@vicons/ionicons5/pinia）与浏览器同源解析到
// index.html importmap 指向的物理文件（剥离 ?v= 版本查询）。
import { register } from 'node:module';

register('./importmap-hooks.mjs', import.meta.url);