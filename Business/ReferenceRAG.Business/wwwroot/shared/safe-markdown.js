import {marked} from '../vendor/marked.js';
const tags=new Set('P BR HR H1 H2 H3 H4 H5 H6 STRONG EM DEL S PRE CODE BLOCKQUOTE UL OL LI TABLE THEAD TBODY TR TH TD A SPAN DIV'.split(' '));
export function renderMarkdown(text) {
  const template=document.createElement('template');template.innerHTML=marked.parse(text||'');
  for(const element of [...template.content.querySelectorAll('*')]) {
    if(!tags.has(element.tagName)){element.remove();continue;}
    for(const attr of [...element.attributes]) {
      if(element.tagName==='A' && attr.name==='href') {
        try {const uri=new URL(attr.value,location.href);if(!['http:','https:','mailto:','obsidian:'].includes(uri.protocol))element.removeAttribute(attr.name);}
        catch{element.removeAttribute(attr.name);}
      } else if(attr.name!=='title') element.removeAttribute(attr.name);
    }
    if(element.tagName==='A'){element.setAttribute('target','_blank');element.setAttribute('rel','noopener noreferrer');}
  }
  return template.innerHTML;
}
