// The native host must not apply silent-start visibility before Vue commits the DOM.
export async function notifyDesktopReady(nextTick, bridge,
  frame = globalThis.requestAnimationFrame || (callback => setTimeout(callback, 0))) {
  await nextTick();
  await new Promise(resolve => frame(() => frame(resolve)));
  bridge?.postMessage(JSON.stringify({id:'desktop-ready',command:'Post',version:2,data:'ready'}));
}
