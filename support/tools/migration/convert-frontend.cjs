// One-time source migration. Not part of build, publish, or application startup.
const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const root = path.resolve(__dirname, '../..');
const requireOld = createRequire(path.join(root, 'dashboard-vue/package.json'));
const sfc = requireOld('@vue/compiler-sfc');
const ts = requireOld('typescript');
const target = path.join(root, 'Business/ReferenceRAG.Business/wwwroot');
const source = path.join(root, 'dashboard-vue/src');
const icons = new Set();
const styles = [];
const imports = new Set();
function write(relative, text) {
  const destination = path.resolve(target, relative);
  if (!destination.startsWith(target + path.sep)) throw Error('Outside migration target');
  fs.mkdirSync(path.dirname(destination), {recursive: true});
  if (fs.existsSync(destination) && fs.readFileSync(destination,'utf8') === text) return;
  fs.writeFileSync(destination, text, {encoding: 'utf8', flag: 'wx'});
}
function rewrite(text) {
  return text.replace(/(['"])@\/([^'"]+)\1/g, (_, quote, spec) => {
    const resolved = /\.(vue|ts)$/.test(spec) ? spec.replace(/\.(vue|ts)$/, '.js') :
      fs.existsSync(path.join(source, spec + '.ts')) ? spec + '.js' : spec + '/index.js';
    return quote + '/app/' + resolved + quote;
  }).replace(/(['"])(\.\.?\/[^'"]+)\1/g, (all, q, spec) => {
    if (/\.(vue|ts)$/.test(spec)) return q + spec.replace(/\.(vue|ts)$/, '.js') + q;
    if (!path.extname(spec)) return q + spec + '/index.js' + q;
    return all;
  }).replaceAll('import.meta.env.DEV', 'false');
}
function emitJS(text, file) {
  const result = ts.transpileModule(text, {fileName: file, compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext, verbatimModuleSyntax: false
  }, reportDiagnostics: true});
  if (result.diagnostics?.some(d => d.category === ts.DiagnosticCategory.Error)) throw Error('Invalid TS: ' + file);
  const output = rewrite(result.outputText);
  const ast = ts.createSourceFile(file, output, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  for (const statement of ast.statements) {
    if (ts.isImportDeclaration(statement)) {
      imports.add(statement.moduleSpecifier.text);
      if (statement.moduleSpecifier.text === '@vicons/ionicons5') {
        for (const item of statement.importClause.namedBindings.elements) icons.add((item.propertyName ?? item.name).text);
      }
    }
  }
  return output;
}
function visit(directory) {
  for (const entry of fs.readdirSync(directory, {withFileTypes:true})) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) { visit(full); continue; }
    if (entry.name.endsWith('.d.ts') || full.includes(path.sep + 'types' + path.sep) || entry.name === 'main.ts') continue;
    const rel = path.relative(source, full).replaceAll('\\', '/');
    if (entry.name.endsWith('.vue')) {
      const descriptor = sfc.parse(fs.readFileSync(full, 'utf8'), {filename:full}).descriptor;
      const id = 'data-v-' + require('node:crypto').createHash('sha256').update(rel).digest('hex').slice(0,8);
      let script = sfc.compileScript(descriptor, {id, genDefaultAs:'component', inlineTemplate:false}).content;
      // Runtime templates consume the returned setup state directly.
      script = script.replace(/Object\.defineProperty\(__returned__, '__isScriptSetup', \{ enumerable: false, value: true \}\)/g, '');
      const existing = new Set([...script.matchAll(/import\s*\{([^}]+)\}\s*from\s*['"]vue['"]/g)]
        .flatMap(m=>m[1].split(',').map(s=>s.trim().split(/\s+as\s+/).pop())));
      const auto = ['ref','computed','reactive','watch','watchEffect','onMounted','onUnmounted','nextTick','h'];
      const missing = auto.filter(name=>!existing.has(name) && new RegExp('(?<![\\w.])'+name+'\\s*\\(').test(script));
      if(missing.length) script = `import { ${missing.join(', ')} } from 'vue';\n` + script;
      if (!/import.*useMessage/.test(script) && /\buseMessage\(/.test(script)) script = "import { useMessage } from 'naive-ui';\n" + script;
      if (!/import.*useDialog/.test(script) && /\buseDialog\(/.test(script)) script = "import { useDialog } from 'naive-ui';\n" + script;
      let output = emitJS(script, full);
      output += '\ncomponent.template = ' + JSON.stringify(descriptor.template?.content ?? '') + ';\n';
      if(descriptor.styles.some(s=>s.scoped)) output += `component.__scopeId = ${JSON.stringify(id)};\n`;
      output += 'export default component;\n';
      write('app/'+rel.replace('.vue','.js'), output);
      for(const style of descriptor.styles) {
        const result = sfc.compileStyle({source:style.content, filename:full, id, scoped:style.scoped});
        if(result.errors.length) throw result.errors[0];
        styles.push(`/* ${rel} */\n${result.code}`);
      }
    } else if(entry.name.endsWith('.ts')) {
      if(rel === 'config/env.ts') continue;
      write('app/'+rel.replace(/\.ts$/,'.js'), emitJS(fs.readFileSync(full,'utf8'),full));
    }
  }
}
visit(source);
write('styles/pages.css', styles.join('\n'));
function vendor(relative, destination) { write('vendor/'+destination, fs.readFileSync(path.join(root,'dashboard-vue/node_modules',relative),'utf8')); }
vendor('vue/dist/vue.esm-browser.prod.js', 'vue.js');
vendor('naive-ui/dist/index.prod.mjs', 'naive-ui.js');
vendor('vue-router/dist/vue-router.esm-browser.prod.js', 'vue-router.js');
vendor('axios/dist/esm/axios.min.js', 'axios.js');
vendor('marked/lib/marked.esm.js', 'marked.js');
// Only icons used by the migrated source are vendored.
for(const name of icons) vendor('@vicons/ionicons5/es/'+name+'.js', 'icons/'+name+'.js');
write('vendor/icons.js', [...icons].sort().map(name=>`export {default as ${name}} from './icons/${name}.js';`).join('\n'));
write('vendor/signalr.js', fs.readFileSync(requireOld.resolve('@microsoft/signalr/dist/browser/signalr.min.js'),'utf8'));
write('vendor/signalr-esm.js', 'export const HubConnectionBuilder = globalThis.signalR.HubConnectionBuilder;\n');
for (const name of ['vue','naive-ui','vue-router','axios','marked','@microsoft/signalr','@vicons/ionicons5']) {
  const dir = path.join(root,'dashboard-vue/node_modules',name);
  const license = fs.readdirSync(dir).find(n=>/^licen[cs]e/i.test(n));
  if(license) write('vendor/licenses/'+name.replaceAll('/','-')+'.txt',fs.readFileSync(path.join(dir,license),'utf8'));
}
write('vendor/README.md', '# Local browser dependencies\n\n'+['vue','naive-ui','vue-router','axios','marked','@microsoft/signalr','@vicons/ionicons5'].map(name=>{
  const pkg=JSON.parse(fs.readFileSync(path.join(root,'dashboard-vue/node_modules',name,'package.json'),'utf8'));
  return `- ${name} ${pkg.version} (${pkg.license}); copied from the installed browser distribution.`;
}).join('\n')+'\n\nNo CDN or frontend build is used at runtime or publish. Licenses are in licenses/.\n');
console.log(JSON.stringify({icons:icons.size,imports:[...imports],target}));
