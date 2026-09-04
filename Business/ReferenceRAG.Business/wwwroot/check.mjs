import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import {compile} from './vendor/vue.js';
const root=path.dirname(fileURLToPath(import.meta.url));
let files=0,templates=0;
function visit(directory) {
  for(const entry of fs.readdirSync(directory,{withFileTypes:true})) {
    if(['vendor','tests','node_modules'].includes(entry.name)) continue;
    const full=path.join(directory,entry.name);
    if(entry.isDirectory()){visit(full);continue;}
    if(!entry.name.endsWith('.js'))continue;
    const result=spawnSync(process.execPath,['--check',full],{encoding:'utf8'});
    if(result.status!==0) throw new Error(result.stderr);
    const source=fs.readFileSync(full,'utf8');files++;
    for(const match of source.matchAll(/(?:from\s*|import\s*\()(['"])(\/[^'"]+)\1/g)) {
      if(!fs.existsSync(path.join(root,match[2]))) throw new Error('Missing local dependency: '+match[2]);
    }
    const template=source.match(/^component\.template = (".*");$/m);
    if(template){compile(JSON.parse(template[1]),{decodeEntities:value=>value});templates++;}
  }
}
visit(root);
console.log(`Validated ${files} JS modules and ${templates} executable Vue templates; no build output generated.`);
