import {reactive,effectScope} from 'vue';
// The two feature stores only need shared reactive state and lifecycle ownership.
// No Pinia compiler/plugin or development-tool dependency is shipped.
let active;
export function createPinia() {
  const stores=new Map(),scope=effectScope(true);
  const container={stores,scope,install(app){active=container; app.onUnmount(()=>{scope.stop();stores.clear();});}};
  return container;
}
export function defineStore(id,setup) {
  return ()=>{
    if(!active) throw new Error('Application state is not initialized');
    if(!active.stores.has(id)) active.stores.set(id,active.scope.run(()=>reactive(setup())));
    return active.stores.get(id);
  };
}
