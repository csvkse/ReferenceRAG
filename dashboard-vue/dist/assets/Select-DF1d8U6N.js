import{U as Ie,c as $,r as T,D as mt,d as fe,G as yt,h as l,X as Lt,_ as Qn,s as ht,bq as Jn,bb as eo,aN as Dt,aQ as to,t as ae,aM as ut,br as kt,w as Fe,az as on,bs as no,e as P,a as ee,i as x,aJ as oo,aK as lo,m as ot,u as ln,L as ft,Q as tt,l as rn,g as oe,O as Ee,k as an,ap as nt,aL as sn,S as dn,p as Ct,v as Ht,q as Le,x as St,bt as ro,n as wt,z as Re,aY as lt,bu as io,V as un,Y as Vt,bv as ao,C as so,bw as uo,bx as co,ay as cn,aZ as fo,aB as Ut,M as Z,aC as Gt,aD as ho,a8 as vo,a9 as go,by as qt,aA as po,bz as bo,bA as mo,bB as wo}from"./index-Brgn67xP.js";import{a as xo,_ as Mt,u as fn}from"./index-BldPXfOE.js";import{e as yo,f as Bt,j as Kt,i as ct,k as Co,l as So,V as Yt,N as Ro,g as Xt,u as Wt,B as Fo,a as zo,b as To,d as Nt,c as _o}from"./Popover-kalenhAK.js";import{b as Po,u as Oo}from"./Space-DIu7bKnh.js";function Zt(e){return e&-e}class hn{constructor(r,a){this.l=r,this.min=a;const s=new Array(r+1);for(let d=0;d<r+1;++d)s[d]=0;this.ft=s}add(r,a){if(a===0)return;const{l:s,ft:d}=this;for(r+=1;r<=s;)d[r]+=a,r+=Zt(r)}get(r){return this.sum(r+1)-this.sum(r)}sum(r){if(r===void 0&&(r=this.l),r<=0)return 0;const{ft:a,min:s,l:d}=this;if(r>d)throw new Error("[FinweckTree.sum]: `i` is larger than length.");let f=r*s;for(;r>0;)f+=a[r],r-=Zt(r);return f}getBound(r){let a=0,s=this.l;for(;s>a;){const d=Math.floor((a+s)/2),f=this.sum(d);if(f>r){s=d;continue}else if(f<r){if(a===d)return this.sum(a+1)<=r?a+1:d;a=d}else return d}return a}}let pt;function Io(){return typeof document>"u"?!1:(pt===void 0&&("matchMedia"in window?pt=window.matchMedia("(pointer:coarse)").matches:pt=!1),pt)}let $t;function Qt(){return typeof document>"u"?1:($t===void 0&&($t="chrome"in window?window.devicePixelRatio:1),$t)}const vn="VVirtualListXScroll";function ko({columnsRef:e,renderColRef:r,renderItemWithColsRef:a}){const s=T(0),d=T(0),f=$(()=>{const R=e.value;if(R.length===0)return null;const O=new hn(R.length,0);return R.forEach((y,k)=>{O.add(k,y.width)}),O}),g=Ie(()=>{const R=f.value;return R!==null?Math.max(R.getBound(d.value)-1,0):0}),n=R=>{const O=f.value;return O!==null?O.sum(R):0},b=Ie(()=>{const R=f.value;return R!==null?Math.min(R.getBound(d.value+s.value)+1,e.value.length-1):0});return mt(vn,{startIndexRef:g,endIndexRef:b,columnsRef:e,renderColRef:r,renderItemWithColsRef:a,getLeft:n}),{listWidthRef:s,scrollLeftRef:d}}const Jt=fe({name:"VirtualListRow",props:{index:{type:Number,required:!0},item:{type:Object,required:!0}},setup(){const{startIndexRef:e,endIndexRef:r,columnsRef:a,getLeft:s,renderColRef:d,renderItemWithColsRef:f}=yt(vn);return{startIndex:e,endIndex:r,columns:a,renderCol:d,renderItemWithCols:f,getLeft:s}},render(){const{startIndex:e,endIndex:r,columns:a,renderCol:s,renderItemWithCols:d,getLeft:f,item:g}=this;if(d!=null)return d({itemIndex:this.index,startColIndex:e,endColIndex:r,allColumns:a,item:g,getLeft:f});if(s!=null){const n=[];for(let b=e;b<=r;++b){const R=a[b];n.push(s({column:R,left:f(b),item:g}))}return n}return null}}),Mo=Bt(".v-vl",{maxHeight:"inherit",height:"100%",overflow:"auto",minWidth:"1px"},[Bt("&:not(.v-vl--show-scrollbar)",{scrollbarWidth:"none"},[Bt("&::-webkit-scrollbar, &::-webkit-scrollbar-track-piece, &::-webkit-scrollbar-thumb",{width:0,height:0,display:"none"})])]),Bo=fe({name:"VirtualList",inheritAttrs:!1,props:{showScrollbar:{type:Boolean,default:!0},columns:{type:Array,default:()=>[]},renderCol:Function,renderItemWithCols:Function,items:{type:Array,default:()=>[]},itemSize:{type:Number,required:!0},itemResizable:Boolean,itemsStyle:[String,Object],visibleItemsTag:{type:[String,Object],default:"div"},visibleItemsProps:Object,ignoreItemResize:Boolean,onScroll:Function,onWheel:Function,onResize:Function,defaultScrollKey:[Number,String],defaultScrollIndex:Number,keyField:{type:String,default:"key"},paddingTop:{type:[Number,String],default:0},paddingBottom:{type:[Number,String],default:0}},setup(e){const r=to();Mo.mount({id:"vueuc/virtual-list",head:!0,anchorMetaName:yo,ssr:r}),ht(()=>{const{defaultScrollIndex:v,defaultScrollKey:z}=e;v!=null?D({index:v}):z!=null&&D({key:z})});let a=!1,s=!1;Jn(()=>{if(a=!1,!s){s=!0;return}D({top:C.value,left:g.value})}),eo(()=>{a=!0,s||(s=!0)});const d=Ie(()=>{if(e.renderCol==null&&e.renderItemWithCols==null||e.columns.length===0)return;let v=0;return e.columns.forEach(z=>{v+=z.width}),v}),f=$(()=>{const v=new Map,{keyField:z}=e;return e.items.forEach((V,j)=>{v.set(V[z],j)}),v}),{scrollLeftRef:g,listWidthRef:n}=ko({columnsRef:ae(e,"columns"),renderColRef:ae(e,"renderCol"),renderItemWithColsRef:ae(e,"renderItemWithCols")}),b=T(null),R=T(void 0),O=new Map,y=$(()=>{const{items:v,itemSize:z,keyField:V}=e,j=new hn(v.length,z);return v.forEach((q,Q)=>{const W=q[V],ne=O.get(W);ne!==void 0&&j.add(Q,ne)}),j}),k=T(0),C=T(0),h=Ie(()=>Math.max(y.value.getBound(C.value-Dt(e.paddingTop))-1,0)),F=$(()=>{const{value:v}=R;if(v===void 0)return[];const{items:z,itemSize:V}=e,j=h.value,q=Math.min(j+Math.ceil(v/V+1),z.length-1),Q=[];for(let W=j;W<=q;++W)Q.push(z[W]);return Q}),D=(v,z)=>{if(typeof v=="number"){te(v,z,"auto");return}const{left:V,top:j,index:q,key:Q,position:W,behavior:ne,debounce:J=!0}=v;if(V!==void 0||j!==void 0)te(V,j,ne);else if(q!==void 0)N(q,ne,J);else if(Q!==void 0){const ce=f.value.get(Q);ce!==void 0&&N(ce,ne,J)}else W==="bottom"?te(0,Number.MAX_SAFE_INTEGER,ne):W==="top"&&te(0,0,ne)};let I,E=null;function N(v,z,V){const{value:j}=y,q=j.sum(v)+Dt(e.paddingTop);if(!V)b.value.scrollTo({left:0,top:q,behavior:z});else{I=v,E!==null&&window.clearTimeout(E),E=window.setTimeout(()=>{I=void 0,E=null},16);const{scrollTop:Q,offsetHeight:W}=b.value;if(q>Q){const ne=j.get(v);q+ne<=Q+W||b.value.scrollTo({left:0,top:q+ne-W,behavior:z})}else b.value.scrollTo({left:0,top:q,behavior:z})}}function te(v,z,V){b.value.scrollTo({left:v,top:z,behavior:V})}function Y(v,z){var V,j,q;if(a||e.ignoreItemResize||de(z.target))return;const{value:Q}=y,W=f.value.get(v),ne=Q.get(W),J=(q=(j=(V=z.borderBoxSize)===null||V===void 0?void 0:V[0])===null||j===void 0?void 0:j.blockSize)!==null&&q!==void 0?q:z.contentRect.height;if(J===ne)return;J-e.itemSize===0?O.delete(v):O.set(v,J-e.itemSize);const ge=J-ne;if(ge===0)return;Q.add(W,ge);const u=b.value;if(u!=null){if(I===void 0){const m=Q.sum(W);u.scrollTop>m&&u.scrollBy(0,ge)}else if(W<I)u.scrollBy(0,ge);else if(W===I){const m=Q.sum(W);J+m>u.scrollTop+u.offsetHeight&&u.scrollBy(0,ge)}le()}k.value++}const K=!Io();let he=!1;function se(v){var z;(z=e.onScroll)===null||z===void 0||z.call(e,v),(!K||!he)&&le()}function ve(v){var z;if((z=e.onWheel)===null||z===void 0||z.call(e,v),K){const V=b.value;if(V!=null){if(v.deltaX===0&&(V.scrollTop===0&&v.deltaY<=0||V.scrollTop+V.offsetHeight>=V.scrollHeight&&v.deltaY>=0))return;v.preventDefault(),V.scrollTop+=v.deltaY/Qt(),V.scrollLeft+=v.deltaX/Qt(),le(),he=!0,Po(()=>{he=!1})}}}function ue(v){if(a||de(v.target))return;if(e.renderCol==null&&e.renderItemWithCols==null){if(v.contentRect.height===R.value)return}else if(v.contentRect.height===R.value&&v.contentRect.width===n.value)return;R.value=v.contentRect.height,n.value=v.contentRect.width;const{onResize:z}=e;z!==void 0&&z(v)}function le(){const{value:v}=b;v!=null&&(C.value=v.scrollTop,g.value=v.scrollLeft)}function de(v){let z=v;for(;z!==null;){if(z.style.display==="none")return!0;z=z.parentElement}return!1}return{listHeight:R,listStyle:{overflow:"auto"},keyToIndex:f,itemsStyle:$(()=>{const{itemResizable:v}=e,z=ut(y.value.sum());return k.value,[e.itemsStyle,{boxSizing:"content-box",width:ut(d.value),height:v?"":z,minHeight:v?z:"",paddingTop:ut(e.paddingTop),paddingBottom:ut(e.paddingBottom)}]}),visibleItemsStyle:$(()=>(k.value,{transform:`translateY(${ut(y.value.sum(h.value))})`})),viewportItems:F,listElRef:b,itemsElRef:T(null),scrollTo:D,handleListResize:ue,handleListScroll:se,handleListWheel:ve,handleItemResize:Y}},render(){const{itemResizable:e,keyField:r,keyToIndex:a,visibleItemsTag:s}=this;return l(Lt,{onResize:this.handleListResize},{default:()=>{var d,f;return l("div",Qn(this.$attrs,{class:["v-vl",this.showScrollbar&&"v-vl--show-scrollbar"],onScroll:this.handleListScroll,onWheel:this.handleListWheel,ref:"listElRef"}),[this.items.length!==0?l("div",{ref:"itemsElRef",class:"v-vl-items",style:this.itemsStyle},[l(s,Object.assign({class:"v-vl-visible-items",style:this.visibleItemsStyle},this.visibleItemsProps),{default:()=>{const{renderCol:g,renderItemWithCols:n}=this;return this.viewportItems.map(b=>{const R=b[r],O=a.get(R),y=g!=null?l(Jt,{index:O,item:b}):void 0,k=n!=null?l(Jt,{index:O,item:b}):void 0,C=this.$slots.default({item:b,renderedCols:y,renderedItemWithCols:k,index:O})[0];return e?l(Lt,{key:R,onResize:h=>this.handleItemResize(R,h)},{default:()=>C}):(C.key=R,C)})}})]):(f=(d=this.$slots).empty)===null||f===void 0?void 0:f.call(d)])}})}});function gn(e,r){r&&(ht(()=>{const{value:a}=e;a&&kt.registerHandler(a,r)}),Fe(e,(a,s)=>{s&&kt.unregisterHandler(s)},{deep:!1}),on(()=>{const{value:a}=e;a&&kt.unregisterHandler(a)}))}function At(e){const r=e.filter(a=>a!==void 0);if(r.length!==0)return r.length===1?r[0]:a=>{e.forEach(s=>{s&&s(a)})}}const $o=fe({name:"Checkmark",render(){return l("svg",{xmlns:"http://www.w3.org/2000/svg",viewBox:"0 0 16 16"},l("g",{fill:"none"},l("path",{d:"M14.046 3.486a.75.75 0 0 1-.032 1.06l-7.93 7.474a.85.85 0 0 1-1.188-.022l-2.68-2.72a.75.75 0 1 1 1.068-1.053l2.234 2.267l7.468-7.038a.75.75 0 0 1 1.06.032z",fill:"currentColor"})))}}),Ao=fe({name:"ChevronDown",render(){return l("svg",{viewBox:"0 0 16 16",fill:"none",xmlns:"http://www.w3.org/2000/svg"},l("path",{d:"M3.14645 5.64645C3.34171 5.45118 3.65829 5.45118 3.85355 5.64645L8 9.79289L12.1464 5.64645C12.3417 5.45118 12.6583 5.45118 12.8536 5.64645C13.0488 5.84171 13.0488 6.15829 12.8536 6.35355L8.35355 10.8536C8.15829 11.0488 7.84171 11.0488 7.64645 10.8536L3.14645 6.35355C2.95118 6.15829 2.95118 5.84171 3.14645 5.64645Z",fill:"currentColor"}))}}),Eo=no("clear",()=>l("svg",{viewBox:"0 0 16 16",version:"1.1",xmlns:"http://www.w3.org/2000/svg"},l("g",{stroke:"none","stroke-width":"1",fill:"none","fill-rule":"evenodd"},l("g",{fill:"currentColor","fill-rule":"nonzero"},l("path",{d:"M8,2 C11.3137085,2 14,4.6862915 14,8 C14,11.3137085 11.3137085,14 8,14 C4.6862915,14 2,11.3137085 2,8 C2,4.6862915 4.6862915,2 8,2 Z M6.5343055,5.83859116 C6.33943736,5.70359511 6.07001296,5.72288026 5.89644661,5.89644661 L5.89644661,5.89644661 L5.83859116,5.9656945 C5.70359511,6.16056264 5.72288026,6.42998704 5.89644661,6.60355339 L5.89644661,6.60355339 L7.293,8 L5.89644661,9.39644661 L5.83859116,9.4656945 C5.70359511,9.66056264 5.72288026,9.92998704 5.89644661,10.1035534 L5.89644661,10.1035534 L5.9656945,10.1614088 C6.16056264,10.2964049 6.42998704,10.2771197 6.60355339,10.1035534 L6.60355339,10.1035534 L8,8.707 L9.39644661,10.1035534 L9.4656945,10.1614088 C9.66056264,10.2964049 9.92998704,10.2771197 10.1035534,10.1035534 L10.1035534,10.1035534 L10.1614088,10.0343055 C10.2964049,9.83943736 10.2771197,9.57001296 10.1035534,9.39644661 L10.1035534,9.39644661 L8.707,8 L10.1035534,6.60355339 L10.1614088,6.5343055 C10.2964049,6.33943736 10.2771197,6.07001296 10.1035534,5.89644661 L10.1035534,5.89644661 L10.0343055,5.83859116 C9.83943736,5.70359511 9.57001296,5.72288026 9.39644661,5.89644661 L9.39644661,5.89644661 L8,7.293 L6.60355339,5.89644661 Z"}))))),Lo=fe({name:"Eye",render(){return l("svg",{xmlns:"http://www.w3.org/2000/svg",viewBox:"0 0 512 512"},l("path",{d:"M255.66 112c-77.94 0-157.89 45.11-220.83 135.33a16 16 0 0 0-.27 17.77C82.92 340.8 161.8 400 255.66 400c92.84 0 173.34-59.38 221.79-135.25a16.14 16.14 0 0 0 0-17.47C428.89 172.28 347.8 112 255.66 112z",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"}),l("circle",{cx:"256",cy:"256",r:"80",fill:"none",stroke:"currentColor","stroke-miterlimit":"10","stroke-width":"32"}))}}),Do=fe({name:"EyeOff",render(){return l("svg",{xmlns:"http://www.w3.org/2000/svg",viewBox:"0 0 512 512"},l("path",{d:"M432 448a15.92 15.92 0 0 1-11.31-4.69l-352-352a16 16 0 0 1 22.62-22.62l352 352A16 16 0 0 1 432 448z",fill:"currentColor"}),l("path",{d:"M255.66 384c-41.49 0-81.5-12.28-118.92-36.5c-34.07-22-64.74-53.51-88.7-91v-.08c19.94-28.57 41.78-52.73 65.24-72.21a2 2 0 0 0 .14-2.94L93.5 161.38a2 2 0 0 0-2.71-.12c-24.92 21-48.05 46.76-69.08 76.92a31.92 31.92 0 0 0-.64 35.54c26.41 41.33 60.4 76.14 98.28 100.65C162 402 207.9 416 255.66 416a239.13 239.13 0 0 0 75.8-12.58a2 2 0 0 0 .77-3.31l-21.58-21.58a4 4 0 0 0-3.83-1a204.8 204.8 0 0 1-51.16 6.47z",fill:"currentColor"}),l("path",{d:"M490.84 238.6c-26.46-40.92-60.79-75.68-99.27-100.53C349 110.55 302 96 255.66 96a227.34 227.34 0 0 0-74.89 12.83a2 2 0 0 0-.75 3.31l21.55 21.55a4 4 0 0 0 3.88 1a192.82 192.82 0 0 1 50.21-6.69c40.69 0 80.58 12.43 118.55 37c34.71 22.4 65.74 53.88 89.76 91a.13.13 0 0 1 0 .16a310.72 310.72 0 0 1-64.12 72.73a2 2 0 0 0-.15 2.95l19.9 19.89a2 2 0 0 0 2.7.13a343.49 343.49 0 0 0 68.64-78.48a32.2 32.2 0 0 0-.1-34.78z",fill:"currentColor"}),l("path",{d:"M256 160a95.88 95.88 0 0 0-21.37 2.4a2 2 0 0 0-1 3.38l112.59 112.56a2 2 0 0 0 3.38-1A96 96 0 0 0 256 160z",fill:"currentColor"}),l("path",{d:"M165.78 233.66a2 2 0 0 0-3.38 1a96 96 0 0 0 115 115a2 2 0 0 0 1-3.38z",fill:"currentColor"}))}}),Vo=P("base-clear",`
 flex-shrink: 0;
 height: 1em;
 width: 1em;
 position: relative;
`,[ee(">",[x("clear",`
 font-size: var(--n-clear-size);
 height: 1em;
 width: 1em;
 cursor: pointer;
 color: var(--n-clear-color);
 transition: color .3s var(--n-bezier);
 display: flex;
 `,[ee("&:hover",`
 color: var(--n-clear-color-hover)!important;
 `),ee("&:active",`
 color: var(--n-clear-color-pressed)!important;
 `)]),x("placeholder",`
 display: flex;
 `),x("clear, placeholder",`
 position: absolute;
 left: 50%;
 top: 50%;
 transform: translateX(-50%) translateY(-50%);
 `,[oo({originalTransform:"translateX(-50%) translateY(-50%)",left:"50%",top:"50%"})])])]),jt=fe({name:"BaseClear",props:{clsPrefix:{type:String,required:!0},show:Boolean,onClear:Function},setup(e){return ln("-base-clear",Vo,ae(e,"clsPrefix")),{handleMouseDown(r){r.preventDefault()}}},render(){const{clsPrefix:e}=this;return l("div",{class:`${e}-base-clear`},l(lo,null,{default:()=>{var r,a;return this.show?l("div",{key:"dismiss",class:`${e}-base-clear__clear`,onClick:this.onClear,onMousedown:this.handleMouseDown,"data-clear":!0},ot(this.$slots.icon,()=>[l(ft,{clsPrefix:e},{default:()=>l(Eo,null)})])):l("div",{key:"icon",class:`${e}-base-clear__placeholder`},(a=(r=this.$slots).placeholder)===null||a===void 0?void 0:a.call(r))}}))}}),Wo=fe({props:{onFocus:Function,onBlur:Function},setup(e){return()=>l("div",{style:"width: 0; height: 0",tabindex:0,onFocus:e.onFocus,onBlur:e.onBlur})}}),en=fe({name:"NBaseSelectGroupHeader",props:{clsPrefix:{type:String,required:!0},tmNode:{type:Object,required:!0}},setup(){const{renderLabelRef:e,renderOptionRef:r,labelFieldRef:a,nodePropsRef:s}=yt(Kt);return{labelField:a,nodeProps:s,renderLabel:e,renderOption:r}},render(){const{clsPrefix:e,renderLabel:r,renderOption:a,nodeProps:s,tmNode:{rawNode:d}}=this,f=s==null?void 0:s(d),g=r?r(d,!1):tt(d[this.labelField],d,!1),n=l("div",Object.assign({},f,{class:[`${e}-base-select-group-header`,f==null?void 0:f.class]}),g);return d.render?d.render({node:n,option:d}):a?a({node:n,option:d,selected:!1}):n}});function No(e,r){return l(rn,{name:"fade-in-scale-up-transition"},{default:()=>e?l(ft,{clsPrefix:r,class:`${r}-base-select-option__check`},{default:()=>l($o)}):null})}const tn=fe({name:"NBaseSelectOption",props:{clsPrefix:{type:String,required:!0},tmNode:{type:Object,required:!0}},setup(e){const{valueRef:r,pendingTmNodeRef:a,multipleRef:s,valueSetRef:d,renderLabelRef:f,renderOptionRef:g,labelFieldRef:n,valueFieldRef:b,showCheckmarkRef:R,nodePropsRef:O,handleOptionClick:y,handleOptionMouseEnter:k}=yt(Kt),C=Ie(()=>{const{value:I}=a;return I?e.tmNode.key===I.key:!1});function h(I){const{tmNode:E}=e;E.disabled||y(I,E)}function F(I){const{tmNode:E}=e;E.disabled||k(I,E)}function D(I){const{tmNode:E}=e,{value:N}=C;E.disabled||N||k(I,E)}return{multiple:s,isGrouped:Ie(()=>{const{tmNode:I}=e,{parent:E}=I;return E&&E.rawNode.type==="group"}),showCheckmark:R,nodeProps:O,isPending:C,isSelected:Ie(()=>{const{value:I}=r,{value:E}=s;if(I===null)return!1;const N=e.tmNode.rawNode[b.value];if(E){const{value:te}=d;return te.has(N)}else return I===N}),labelField:n,renderLabel:f,renderOption:g,handleMouseMove:D,handleMouseEnter:F,handleClick:h}},render(){const{clsPrefix:e,tmNode:{rawNode:r},isSelected:a,isPending:s,isGrouped:d,showCheckmark:f,nodeProps:g,renderOption:n,renderLabel:b,handleClick:R,handleMouseEnter:O,handleMouseMove:y}=this,k=No(a,e),C=b?[b(r,a),f&&k]:[tt(r[this.labelField],r,a),f&&k],h=g==null?void 0:g(r),F=l("div",Object.assign({},h,{class:[`${e}-base-select-option`,r.class,h==null?void 0:h.class,{[`${e}-base-select-option--disabled`]:r.disabled,[`${e}-base-select-option--selected`]:a,[`${e}-base-select-option--grouped`]:d,[`${e}-base-select-option--pending`]:s,[`${e}-base-select-option--show-checkmark`]:f}],style:[(h==null?void 0:h.style)||"",r.style||""],onClick:At([R,h==null?void 0:h.onClick]),onMouseenter:At([O,h==null?void 0:h.onMouseenter]),onMousemove:At([y,h==null?void 0:h.onMousemove])}),l("div",{class:`${e}-base-select-option__content`},C));return r.render?r.render({node:F,option:r,selected:a}):n?n({node:F,option:r,selected:a}):F}}),jo=P("base-select-menu",`
 line-height: 1.5;
 outline: none;
 z-index: 0;
 position: relative;
 border-radius: var(--n-border-radius);
 transition:
 background-color .3s var(--n-bezier),
 box-shadow .3s var(--n-bezier);
 background-color: var(--n-color);
`,[P("scrollbar",`
 max-height: var(--n-height);
 `),P("virtual-list",`
 max-height: var(--n-height);
 `),P("base-select-option",`
 min-height: var(--n-option-height);
 font-size: var(--n-option-font-size);
 display: flex;
 align-items: center;
 `,[x("content",`
 z-index: 1;
 white-space: nowrap;
 text-overflow: ellipsis;
 overflow: hidden;
 `)]),P("base-select-group-header",`
 min-height: var(--n-option-height);
 font-size: .93em;
 display: flex;
 align-items: center;
 `),P("base-select-menu-option-wrapper",`
 position: relative;
 width: 100%;
 `),x("loading, empty",`
 display: flex;
 padding: 12px 32px;
 flex: 1;
 justify-content: center;
 `),x("loading",`
 color: var(--n-loading-color);
 font-size: var(--n-loading-size);
 `),x("header",`
 padding: 8px var(--n-option-padding-left);
 font-size: var(--n-option-font-size);
 transition: 
 color .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 border-bottom: 1px solid var(--n-action-divider-color);
 color: var(--n-action-text-color);
 `),x("action",`
 padding: 8px var(--n-option-padding-left);
 font-size: var(--n-option-font-size);
 transition: 
 color .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 border-top: 1px solid var(--n-action-divider-color);
 color: var(--n-action-text-color);
 `),P("base-select-group-header",`
 position: relative;
 cursor: default;
 padding: var(--n-option-padding);
 color: var(--n-group-header-text-color);
 `),P("base-select-option",`
 cursor: pointer;
 position: relative;
 padding: var(--n-option-padding);
 transition:
 color .3s var(--n-bezier),
 opacity .3s var(--n-bezier);
 box-sizing: border-box;
 color: var(--n-option-text-color);
 opacity: 1;
 `,[oe("show-checkmark",`
 padding-right: calc(var(--n-option-padding-right) + 20px);
 `),ee("&::before",`
 content: "";
 position: absolute;
 left: 4px;
 right: 4px;
 top: 0;
 bottom: 0;
 border-radius: var(--n-border-radius);
 transition: background-color .3s var(--n-bezier);
 `),ee("&:active",`
 color: var(--n-option-text-color-pressed);
 `),oe("grouped",`
 padding-left: calc(var(--n-option-padding-left) * 1.5);
 `),oe("pending",[ee("&::before",`
 background-color: var(--n-option-color-pending);
 `)]),oe("selected",`
 color: var(--n-option-text-color-active);
 `,[ee("&::before",`
 background-color: var(--n-option-color-active);
 `),oe("pending",[ee("&::before",`
 background-color: var(--n-option-color-active-pending);
 `)])]),oe("disabled",`
 cursor: not-allowed;
 `,[Ee("selected",`
 color: var(--n-option-text-color-disabled);
 `),oe("selected",`
 opacity: var(--n-option-opacity-disabled);
 `)]),x("check",`
 font-size: 16px;
 position: absolute;
 right: calc(var(--n-option-padding-right) - 4px);
 top: calc(50% - 7px);
 color: var(--n-option-check-color);
 transition: color .3s var(--n-bezier);
 `,[an({enterScale:"0.5"})])])]),Ho=fe({name:"InternalSelectMenu",props:Object.assign(Object.assign({},Le.props),{clsPrefix:{type:String,required:!0},scrollable:{type:Boolean,default:!0},treeMate:{type:Object,required:!0},multiple:Boolean,size:{type:String,default:"medium"},value:{type:[String,Number,Array],default:null},autoPending:Boolean,virtualScroll:{type:Boolean,default:!0},show:{type:Boolean,default:!0},labelField:{type:String,default:"label"},valueField:{type:String,default:"value"},loading:Boolean,focusable:Boolean,renderLabel:Function,renderOption:Function,nodeProps:Function,showCheckmark:{type:Boolean,default:!0},onMousedown:Function,onScroll:Function,onFocus:Function,onBlur:Function,onKeyup:Function,onKeydown:Function,onTabOut:Function,onMouseenter:Function,onMouseleave:Function,onResize:Function,resetMenuOnOptionsChange:{type:Boolean,default:!0},inlineThemeDisabled:Boolean,scrollbarProps:Object,onToggle:Function}),setup(e){const{mergedClsPrefixRef:r,mergedRtlRef:a,mergedComponentPropsRef:s}=Ct(e),d=Ht("InternalSelectMenu",a,r),f=Le("InternalSelectMenu","-internal-select-menu",jo,ro,e,ae(e,"clsPrefix")),g=T(null),n=T(null),b=T(null),R=$(()=>e.treeMate.getFlattenedNodes()),O=$(()=>Co(R.value)),y=T(null);function k(){const{treeMate:u}=e;let m=null;const{value:X}=e;X===null?m=u.getFirstAvailableNode():(e.multiple?m=u.getNode((X||[])[(X||[]).length-1]):m=u.getNode(X),(!m||m.disabled)&&(m=u.getFirstAvailableNode())),j(m||null)}function C(){const{value:u}=y;u&&!e.treeMate.getNode(u.key)&&(y.value=null)}let h;Fe(()=>e.show,u=>{u?h=Fe(()=>e.treeMate,()=>{e.resetMenuOnOptionsChange?(e.autoPending?k():C(),wt(q)):C()},{immediate:!0}):h==null||h()},{immediate:!0}),on(()=>{h==null||h()});const F=$(()=>Dt(f.value.self[Re("optionHeight",e.size)])),D=$(()=>lt(f.value.self[Re("padding",e.size)])),I=$(()=>e.multiple&&Array.isArray(e.value)?new Set(e.value):new Set),E=$(()=>{const u=R.value;return u&&u.length===0}),N=$(()=>{var u,m;return(m=(u=s==null?void 0:s.value)===null||u===void 0?void 0:u.Select)===null||m===void 0?void 0:m.renderEmpty});function te(u){const{onToggle:m}=e;m&&m(u)}function Y(u){const{onScroll:m}=e;m&&m(u)}function K(u){var m;(m=b.value)===null||m===void 0||m.sync(),Y(u)}function he(){var u;(u=b.value)===null||u===void 0||u.sync()}function se(){const{value:u}=y;return u||null}function ve(u,m){m.disabled||j(m,!1)}function ue(u,m){m.disabled||te(m)}function le(u){var m;ct(u,"action")||(m=e.onKeyup)===null||m===void 0||m.call(e,u)}function de(u){var m;ct(u,"action")||(m=e.onKeydown)===null||m===void 0||m.call(e,u)}function v(u){var m;(m=e.onMousedown)===null||m===void 0||m.call(e,u),!e.focusable&&u.preventDefault()}function z(){const{value:u}=y;u&&j(u.getNext({loop:!0}),!0)}function V(){const{value:u}=y;u&&j(u.getPrev({loop:!0}),!0)}function j(u,m=!1){y.value=u,m&&q()}function q(){var u,m;const X=y.value;if(!X)return;const we=O.value(X.key);we!==null&&(e.virtualScroll?(u=n.value)===null||u===void 0||u.scrollTo({index:we}):(m=b.value)===null||m===void 0||m.scrollTo({index:we,elSize:F.value}))}function Q(u){var m,X;!((m=g.value)===null||m===void 0)&&m.contains(u.target)&&((X=e.onFocus)===null||X===void 0||X.call(e,u))}function W(u){var m,X;!((m=g.value)===null||m===void 0)&&m.contains(u.relatedTarget)||(X=e.onBlur)===null||X===void 0||X.call(e,u)}mt(Kt,{handleOptionMouseEnter:ve,handleOptionClick:ue,valueSetRef:I,pendingTmNodeRef:y,nodePropsRef:ae(e,"nodeProps"),showCheckmarkRef:ae(e,"showCheckmark"),multipleRef:ae(e,"multiple"),valueRef:ae(e,"value"),renderLabelRef:ae(e,"renderLabel"),renderOptionRef:ae(e,"renderOption"),labelFieldRef:ae(e,"labelField"),valueFieldRef:ae(e,"valueField")}),mt(So,g),ht(()=>{const{value:u}=b;u&&u.sync()});const ne=$(()=>{const{size:u}=e,{common:{cubicBezierEaseInOut:m},self:{height:X,borderRadius:we,color:ke,groupHeaderTextColor:xe,actionDividerColor:pe,optionTextColorPressed:Me,optionTextColor:ye,optionTextColorDisabled:De,optionTextColorActive:Ve,optionOpacityDisabled:We,optionCheckColor:ze,actionTextColor:Te,optionColorPending:Ne,optionColorActive:Ce,loadingColor:je,loadingSize:Be,optionColorActivePending:$e,[Re("optionFontSize",u)]:me,[Re("optionHeight",u)]:c,[Re("optionPadding",u)]:w}}=f.value;return{"--n-height":X,"--n-action-divider-color":pe,"--n-action-text-color":Te,"--n-bezier":m,"--n-border-radius":we,"--n-color":ke,"--n-option-font-size":me,"--n-group-header-text-color":xe,"--n-option-check-color":ze,"--n-option-color-pending":Ne,"--n-option-color-active":Ce,"--n-option-color-active-pending":$e,"--n-option-height":c,"--n-option-opacity-disabled":We,"--n-option-text-color":ye,"--n-option-text-color-active":Ve,"--n-option-text-color-disabled":De,"--n-option-text-color-pressed":Me,"--n-option-padding":w,"--n-option-padding-left":lt(w,"left"),"--n-option-padding-right":lt(w,"right"),"--n-loading-color":je,"--n-loading-size":Be}}),{inlineThemeDisabled:J}=e,ce=J?St("internal-select-menu",$(()=>e.size[0]),ne,e):void 0,ge={selfRef:g,next:z,prev:V,getPendingTmNode:se};return gn(g,e.onResize),Object.assign({mergedTheme:f,mergedClsPrefix:r,rtlEnabled:d,virtualListRef:n,scrollbarRef:b,itemSize:F,padding:D,flattenedNodes:R,empty:E,mergedRenderEmpty:N,virtualListContainer(){const{value:u}=n;return u==null?void 0:u.listElRef},virtualListContent(){const{value:u}=n;return u==null?void 0:u.itemsElRef},doScroll:Y,handleFocusin:Q,handleFocusout:W,handleKeyUp:le,handleKeyDown:de,handleMouseDown:v,handleVirtualListResize:he,handleVirtualListScroll:K,cssVars:J?void 0:ne,themeClass:ce==null?void 0:ce.themeClass,onRender:ce==null?void 0:ce.onRender},ge)},render(){const{$slots:e,virtualScroll:r,clsPrefix:a,mergedTheme:s,themeClass:d,onRender:f}=this;return f==null||f(),l("div",{ref:"selfRef",tabindex:this.focusable?0:-1,class:[`${a}-base-select-menu`,`${a}-base-select-menu--${this.size}-size`,this.rtlEnabled&&`${a}-base-select-menu--rtl`,d,this.multiple&&`${a}-base-select-menu--multiple`],style:this.cssVars,onFocusin:this.handleFocusin,onFocusout:this.handleFocusout,onKeyup:this.handleKeyUp,onKeydown:this.handleKeyDown,onMousedown:this.handleMouseDown,onMouseenter:this.onMouseenter,onMouseleave:this.onMouseleave},nt(e.header,g=>g&&l("div",{class:`${a}-base-select-menu__header`,"data-header":!0,key:"header"},g)),this.loading?l("div",{class:`${a}-base-select-menu__loading`},l(sn,{clsPrefix:a,strokeWidth:20})):this.empty?l("div",{class:`${a}-base-select-menu__empty`,"data-empty":!0},ot(e.empty,()=>{var g;return[((g=this.mergedRenderEmpty)===null||g===void 0?void 0:g.call(this))||l(xo,{theme:s.peers.Empty,themeOverrides:s.peerOverrides.Empty,size:this.size})]})):l(dn,Object.assign({ref:"scrollbarRef",theme:s.peers.Scrollbar,themeOverrides:s.peerOverrides.Scrollbar,scrollable:this.scrollable,container:r?this.virtualListContainer:void 0,content:r?this.virtualListContent:void 0,onScroll:r?void 0:this.doScroll},this.scrollbarProps),{default:()=>r?l(Bo,{ref:"virtualListRef",class:`${a}-virtual-list`,items:this.flattenedNodes,itemSize:this.itemSize,showScrollbar:!1,paddingTop:this.padding.top,paddingBottom:this.padding.bottom,onResize:this.handleVirtualListResize,onScroll:this.handleVirtualListScroll,itemResizable:!0},{default:({item:g})=>g.isGroup?l(en,{key:g.key,clsPrefix:a,tmNode:g}):g.ignored?null:l(tn,{clsPrefix:a,key:g.key,tmNode:g})}):l("div",{class:`${a}-base-select-menu-option-wrapper`,style:{paddingTop:this.padding.top,paddingBottom:this.padding.bottom}},this.flattenedNodes.map(g=>g.isGroup?l(en,{key:g.key,clsPrefix:a,tmNode:g}):l(tn,{clsPrefix:a,key:g.key,tmNode:g})))}),nt(e.action,g=>g&&[l("div",{class:`${a}-base-select-menu__action`,"data-action":!0,key:"action"},g),l(Wo,{onFocus:this.onTabOut,key:"focus-detector"})]))}}),pn=fe({name:"InternalSelectionSuffix",props:{clsPrefix:{type:String,required:!0},showArrow:{type:Boolean,default:void 0},showClear:{type:Boolean,default:void 0},loading:{type:Boolean,default:!1},onClear:Function},setup(e,{slots:r}){return()=>{const{clsPrefix:a}=e;return l(sn,{clsPrefix:a,class:`${a}-base-suffix`,strokeWidth:24,scale:.85,show:e.loading},{default:()=>e.showArrow?l(jt,{clsPrefix:a,show:e.showClear,onClear:e.onClear},{placeholder:()=>l(ft,{clsPrefix:a,class:`${a}-base-suffix__arrow`},{default:()=>ot(r.default,()=>[l(Ao,null)])})}):null})}}}),Ko=ee([P("base-selection",`
 --n-padding-single: var(--n-padding-single-top) var(--n-padding-single-right) var(--n-padding-single-bottom) var(--n-padding-single-left);
 --n-padding-multiple: var(--n-padding-multiple-top) var(--n-padding-multiple-right) var(--n-padding-multiple-bottom) var(--n-padding-multiple-left);
 position: relative;
 z-index: auto;
 box-shadow: none;
 width: 100%;
 max-width: 100%;
 display: inline-block;
 vertical-align: bottom;
 border-radius: var(--n-border-radius);
 min-height: var(--n-height);
 line-height: 1.5;
 font-size: var(--n-font-size);
 `,[P("base-loading",`
 color: var(--n-loading-color);
 `),P("base-selection-tags","min-height: var(--n-height);"),x("border, state-border",`
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 pointer-events: none;
 border: var(--n-border);
 border-radius: inherit;
 transition:
 box-shadow .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 `),x("state-border",`
 z-index: 1;
 border-color: #0000;
 `),P("base-suffix",`
 cursor: pointer;
 position: absolute;
 top: 50%;
 transform: translateY(-50%);
 right: 10px;
 `,[x("arrow",`
 font-size: var(--n-arrow-size);
 color: var(--n-arrow-color);
 transition: color .3s var(--n-bezier);
 `)]),P("base-selection-overlay",`
 display: flex;
 align-items: center;
 white-space: nowrap;
 pointer-events: none;
 position: absolute;
 top: 0;
 right: 0;
 bottom: 0;
 left: 0;
 padding: var(--n-padding-single);
 transition: color .3s var(--n-bezier);
 `,[x("wrapper",`
 flex-basis: 0;
 flex-grow: 1;
 overflow: hidden;
 text-overflow: ellipsis;
 `)]),P("base-selection-placeholder",`
 color: var(--n-placeholder-color);
 `,[x("inner",`
 max-width: 100%;
 overflow: hidden;
 `)]),P("base-selection-tags",`
 cursor: pointer;
 outline: none;
 box-sizing: border-box;
 position: relative;
 z-index: auto;
 display: flex;
 padding: var(--n-padding-multiple);
 flex-wrap: wrap;
 align-items: center;
 width: 100%;
 vertical-align: bottom;
 background-color: var(--n-color);
 border-radius: inherit;
 transition:
 color .3s var(--n-bezier),
 box-shadow .3s var(--n-bezier),
 background-color .3s var(--n-bezier);
 `),P("base-selection-label",`
 height: var(--n-height);
 display: inline-flex;
 width: 100%;
 vertical-align: bottom;
 cursor: pointer;
 outline: none;
 z-index: auto;
 box-sizing: border-box;
 position: relative;
 transition:
 color .3s var(--n-bezier),
 box-shadow .3s var(--n-bezier),
 background-color .3s var(--n-bezier);
 border-radius: inherit;
 background-color: var(--n-color);
 align-items: center;
 `,[P("base-selection-input",`
 font-size: inherit;
 line-height: inherit;
 outline: none;
 cursor: pointer;
 box-sizing: border-box;
 border:none;
 width: 100%;
 padding: var(--n-padding-single);
 background-color: #0000;
 color: var(--n-text-color);
 transition: color .3s var(--n-bezier);
 caret-color: var(--n-caret-color);
 `,[x("content",`
 text-overflow: ellipsis;
 overflow: hidden;
 white-space: nowrap; 
 `)]),x("render-label",`
 color: var(--n-text-color);
 `)]),Ee("disabled",[ee("&:hover",[x("state-border",`
 box-shadow: var(--n-box-shadow-hover);
 border: var(--n-border-hover);
 `)]),oe("focus",[x("state-border",`
 box-shadow: var(--n-box-shadow-focus);
 border: var(--n-border-focus);
 `)]),oe("active",[x("state-border",`
 box-shadow: var(--n-box-shadow-active);
 border: var(--n-border-active);
 `),P("base-selection-label","background-color: var(--n-color-active);"),P("base-selection-tags","background-color: var(--n-color-active);")])]),oe("disabled","cursor: not-allowed;",[x("arrow",`
 color: var(--n-arrow-color-disabled);
 `),P("base-selection-label",`
 cursor: not-allowed;
 background-color: var(--n-color-disabled);
 `,[P("base-selection-input",`
 cursor: not-allowed;
 color: var(--n-text-color-disabled);
 `),x("render-label",`
 color: var(--n-text-color-disabled);
 `)]),P("base-selection-tags",`
 cursor: not-allowed;
 background-color: var(--n-color-disabled);
 `),P("base-selection-placeholder",`
 cursor: not-allowed;
 color: var(--n-placeholder-color-disabled);
 `)]),P("base-selection-input-tag",`
 height: calc(var(--n-height) - 6px);
 line-height: calc(var(--n-height) - 6px);
 outline: none;
 display: none;
 position: relative;
 margin-bottom: 3px;
 max-width: 100%;
 vertical-align: bottom;
 `,[x("input",`
 font-size: inherit;
 font-family: inherit;
 min-width: 1px;
 padding: 0;
 background-color: #0000;
 outline: none;
 border: none;
 max-width: 100%;
 overflow: hidden;
 width: 1em;
 line-height: inherit;
 cursor: pointer;
 color: var(--n-text-color);
 caret-color: var(--n-caret-color);
 `),x("mirror",`
 position: absolute;
 left: 0;
 top: 0;
 white-space: pre;
 visibility: hidden;
 user-select: none;
 -webkit-user-select: none;
 opacity: 0;
 `)]),["warning","error"].map(e=>oe(`${e}-status`,[x("state-border",`border: var(--n-border-${e});`),Ee("disabled",[ee("&:hover",[x("state-border",`
 box-shadow: var(--n-box-shadow-hover-${e});
 border: var(--n-border-hover-${e});
 `)]),oe("active",[x("state-border",`
 box-shadow: var(--n-box-shadow-active-${e});
 border: var(--n-border-active-${e});
 `),P("base-selection-label",`background-color: var(--n-color-active-${e});`),P("base-selection-tags",`background-color: var(--n-color-active-${e});`)]),oe("focus",[x("state-border",`
 box-shadow: var(--n-box-shadow-focus-${e});
 border: var(--n-border-focus-${e});
 `)])])]))]),P("base-selection-popover",`
 margin-bottom: -3px;
 display: flex;
 flex-wrap: wrap;
 margin-right: -8px;
 `),P("base-selection-tag-wrapper",`
 max-width: 100%;
 display: inline-flex;
 padding: 0 7px 3px 0;
 `,[ee("&:last-child","padding-right: 0;"),P("tag",`
 font-size: 14px;
 max-width: 100%;
 `,[x("content",`
 line-height: 1.25;
 text-overflow: ellipsis;
 overflow: hidden;
 `)])])]),Uo=fe({name:"InternalSelection",props:Object.assign(Object.assign({},Le.props),{clsPrefix:{type:String,required:!0},bordered:{type:Boolean,default:void 0},active:Boolean,pattern:{type:String,default:""},placeholder:String,selectedOption:{type:Object,default:null},selectedOptions:{type:Array,default:null},labelField:{type:String,default:"label"},valueField:{type:String,default:"value"},multiple:Boolean,filterable:Boolean,clearable:Boolean,disabled:Boolean,size:{type:String,default:"medium"},loading:Boolean,autofocus:Boolean,showArrow:{type:Boolean,default:!0},inputProps:Object,focused:Boolean,renderTag:Function,onKeydown:Function,onClick:Function,onBlur:Function,onFocus:Function,onDeleteOption:Function,maxTagCount:[String,Number],ellipsisTagPopoverProps:Object,onClear:Function,onPatternInput:Function,onPatternFocus:Function,onPatternBlur:Function,renderLabel:Function,status:String,inlineThemeDisabled:Boolean,ignoreComposition:{type:Boolean,default:!0},onResize:Function}),setup(e){const{mergedClsPrefixRef:r,mergedRtlRef:a}=Ct(e),s=Ht("InternalSelection",a,r),d=T(null),f=T(null),g=T(null),n=T(null),b=T(null),R=T(null),O=T(null),y=T(null),k=T(null),C=T(null),h=T(!1),F=T(!1),D=T(!1),I=Le("InternalSelection","-internal-selection",Ko,ao,e,ae(e,"clsPrefix")),E=$(()=>e.clearable&&!e.disabled&&(D.value||e.active)),N=$(()=>e.selectedOption?e.renderTag?e.renderTag({option:e.selectedOption,handleClose:()=>{}}):e.renderLabel?e.renderLabel(e.selectedOption,!0):tt(e.selectedOption[e.labelField],e.selectedOption,!0):e.placeholder),te=$(()=>{const c=e.selectedOption;if(c)return c[e.labelField]}),Y=$(()=>e.multiple?!!(Array.isArray(e.selectedOptions)&&e.selectedOptions.length):e.selectedOption!==null);function K(){var c;const{value:w}=d;if(w){const{value:re}=f;re&&(re.style.width=`${w.offsetWidth}px`,e.maxTagCount!=="responsive"&&((c=k.value)===null||c===void 0||c.sync({showAllItemsBeforeCalculate:!1})))}}function he(){const{value:c}=C;c&&(c.style.display="none")}function se(){const{value:c}=C;c&&(c.style.display="inline-block")}Fe(ae(e,"active"),c=>{c||he()}),Fe(ae(e,"pattern"),()=>{e.multiple&&wt(K)});function ve(c){const{onFocus:w}=e;w&&w(c)}function ue(c){const{onBlur:w}=e;w&&w(c)}function le(c){const{onDeleteOption:w}=e;w&&w(c)}function de(c){const{onClear:w}=e;w&&w(c)}function v(c){const{onPatternInput:w}=e;w&&w(c)}function z(c){var w;(!c.relatedTarget||!(!((w=g.value)===null||w===void 0)&&w.contains(c.relatedTarget)))&&ve(c)}function V(c){var w;!((w=g.value)===null||w===void 0)&&w.contains(c.relatedTarget)||ue(c)}function j(c){de(c)}function q(){D.value=!0}function Q(){D.value=!1}function W(c){!e.active||!e.filterable||c.target!==f.value&&c.preventDefault()}function ne(c){le(c)}const J=T(!1);function ce(c){if(c.key==="Backspace"&&!J.value&&!e.pattern.length){const{selectedOptions:w}=e;w!=null&&w.length&&ne(w[w.length-1])}}let ge=null;function u(c){const{value:w}=d;if(w){const re=c.target.value;w.textContent=re,K()}e.ignoreComposition&&J.value?ge=c:v(c)}function m(){J.value=!0}function X(){J.value=!1,e.ignoreComposition&&v(ge),ge=null}function we(c){var w;F.value=!0,(w=e.onPatternFocus)===null||w===void 0||w.call(e,c)}function ke(c){var w;F.value=!1,(w=e.onPatternBlur)===null||w===void 0||w.call(e,c)}function xe(){var c,w;if(e.filterable)F.value=!1,(c=R.value)===null||c===void 0||c.blur(),(w=f.value)===null||w===void 0||w.blur();else if(e.multiple){const{value:re}=n;re==null||re.blur()}else{const{value:re}=b;re==null||re.blur()}}function pe(){var c,w,re;e.filterable?(F.value=!1,(c=R.value)===null||c===void 0||c.focus()):e.multiple?(w=n.value)===null||w===void 0||w.focus():(re=b.value)===null||re===void 0||re.focus()}function Me(){const{value:c}=f;c&&(se(),c.focus())}function ye(){const{value:c}=f;c&&c.blur()}function De(c){const{value:w}=O;w&&w.setTextContent(`+${c}`)}function Ve(){const{value:c}=y;return c}function We(){return f.value}let ze=null;function Te(){ze!==null&&window.clearTimeout(ze)}function Ne(){e.active||(Te(),ze=window.setTimeout(()=>{Y.value&&(h.value=!0)},100))}function Ce(){Te()}function je(c){c||(Te(),h.value=!1)}Fe(Y,c=>{c||(h.value=!1)}),ht(()=>{Vt(()=>{const c=R.value;c&&(e.disabled?c.removeAttribute("tabindex"):c.tabIndex=F.value?-1:0)})}),gn(g,e.onResize);const{inlineThemeDisabled:Be}=e,$e=$(()=>{const{size:c}=e,{common:{cubicBezierEaseInOut:w},self:{fontWeight:re,borderRadius:rt,color:it,placeholderColor:Ue,textColor:Ge,paddingSingle:qe,paddingMultiple:Ye,caretColor:at,colorDisabled:st,textColorDisabled:Xe,placeholderColorDisabled:Se,colorActive:o,boxShadowFocus:p,boxShadowActive:_,boxShadowHover:A,border:M,borderFocus:B,borderHover:L,borderActive:ie,arrowColor:be,arrowColorDisabled:Rt,loadingColor:vt,colorActiveWarning:Ft,boxShadowFocusWarning:Ze,boxShadowActiveWarning:Qe,boxShadowHoverWarning:zt,borderWarning:Tt,borderFocusWarning:gt,borderHoverWarning:Ae,borderActiveWarning:t,colorActiveError:i,boxShadowFocusError:S,boxShadowActiveError:U,boxShadowHoverError:G,borderError:H,borderFocusError:_e,borderHoverError:Pe,borderActiveError:Oe,clearColor:He,clearColorHover:Ke,clearColorPressed:dt,clearSize:_t,arrowSize:Pt,[Re("height",c)]:Ot,[Re("fontSize",c)]:It}}=I.value,Je=lt(qe),et=lt(Ye);return{"--n-bezier":w,"--n-border":M,"--n-border-active":ie,"--n-border-focus":B,"--n-border-hover":L,"--n-border-radius":rt,"--n-box-shadow-active":_,"--n-box-shadow-focus":p,"--n-box-shadow-hover":A,"--n-caret-color":at,"--n-color":it,"--n-color-active":o,"--n-color-disabled":st,"--n-font-size":It,"--n-height":Ot,"--n-padding-single-top":Je.top,"--n-padding-multiple-top":et.top,"--n-padding-single-right":Je.right,"--n-padding-multiple-right":et.right,"--n-padding-single-left":Je.left,"--n-padding-multiple-left":et.left,"--n-padding-single-bottom":Je.bottom,"--n-padding-multiple-bottom":et.bottom,"--n-placeholder-color":Ue,"--n-placeholder-color-disabled":Se,"--n-text-color":Ge,"--n-text-color-disabled":Xe,"--n-arrow-color":be,"--n-arrow-color-disabled":Rt,"--n-loading-color":vt,"--n-color-active-warning":Ft,"--n-box-shadow-focus-warning":Ze,"--n-box-shadow-active-warning":Qe,"--n-box-shadow-hover-warning":zt,"--n-border-warning":Tt,"--n-border-focus-warning":gt,"--n-border-hover-warning":Ae,"--n-border-active-warning":t,"--n-color-active-error":i,"--n-box-shadow-focus-error":S,"--n-box-shadow-active-error":U,"--n-box-shadow-hover-error":G,"--n-border-error":H,"--n-border-focus-error":_e,"--n-border-hover-error":Pe,"--n-border-active-error":Oe,"--n-clear-size":_t,"--n-clear-color":He,"--n-clear-color-hover":Ke,"--n-clear-color-pressed":dt,"--n-arrow-size":Pt,"--n-font-weight":re}}),me=Be?St("internal-selection",$(()=>e.size[0]),$e,e):void 0;return{mergedTheme:I,mergedClearable:E,mergedClsPrefix:r,rtlEnabled:s,patternInputFocused:F,filterablePlaceholder:N,label:te,selected:Y,showTagsPanel:h,isComposing:J,counterRef:O,counterWrapperRef:y,patternInputMirrorRef:d,patternInputRef:f,selfRef:g,multipleElRef:n,singleElRef:b,patternInputWrapperRef:R,overflowRef:k,inputTagElRef:C,handleMouseDown:W,handleFocusin:z,handleClear:j,handleMouseEnter:q,handleMouseLeave:Q,handleDeleteOption:ne,handlePatternKeyDown:ce,handlePatternInputInput:u,handlePatternInputBlur:ke,handlePatternInputFocus:we,handleMouseEnterCounter:Ne,handleMouseLeaveCounter:Ce,handleFocusout:V,handleCompositionEnd:X,handleCompositionStart:m,onPopoverUpdateShow:je,focus:pe,focusInput:Me,blur:xe,blurInput:ye,updateCounter:De,getCounter:Ve,getTail:We,renderLabel:e.renderLabel,cssVars:Be?void 0:$e,themeClass:me==null?void 0:me.themeClass,onRender:me==null?void 0:me.onRender}},render(){const{status:e,multiple:r,size:a,disabled:s,filterable:d,maxTagCount:f,bordered:g,clsPrefix:n,ellipsisTagPopoverProps:b,onRender:R,renderTag:O,renderLabel:y}=this;R==null||R();const k=f==="responsive",C=typeof f=="number",h=k||C,F=l(io,null,{default:()=>l(pn,{clsPrefix:n,loading:this.loading,showArrow:this.showArrow,showClear:this.mergedClearable&&this.selected,onClear:this.handleClear},{default:()=>{var I,E;return(E=(I=this.$slots).arrow)===null||E===void 0?void 0:E.call(I)}})});let D;if(r){const{labelField:I}=this,E=v=>l("div",{class:`${n}-base-selection-tag-wrapper`,key:v.value},O?O({option:v,handleClose:()=>{this.handleDeleteOption(v)}}):l(Mt,{size:a,closable:!v.disabled,disabled:s,onClose:()=>{this.handleDeleteOption(v)},internalCloseIsButtonTag:!1,internalCloseFocusable:!1},{default:()=>y?y(v,!0):tt(v[I],v,!0)})),N=()=>(C?this.selectedOptions.slice(0,f):this.selectedOptions).map(E),te=d?l("div",{class:`${n}-base-selection-input-tag`,ref:"inputTagElRef",key:"__input-tag__"},l("input",Object.assign({},this.inputProps,{ref:"patternInputRef",tabindex:-1,disabled:s,value:this.pattern,autofocus:this.autofocus,class:`${n}-base-selection-input-tag__input`,onBlur:this.handlePatternInputBlur,onFocus:this.handlePatternInputFocus,onKeydown:this.handlePatternKeyDown,onInput:this.handlePatternInputInput,onCompositionstart:this.handleCompositionStart,onCompositionend:this.handleCompositionEnd})),l("span",{ref:"patternInputMirrorRef",class:`${n}-base-selection-input-tag__mirror`},this.pattern)):null,Y=k?()=>l("div",{class:`${n}-base-selection-tag-wrapper`,ref:"counterWrapperRef"},l(Mt,{size:a,ref:"counterRef",onMouseenter:this.handleMouseEnterCounter,onMouseleave:this.handleMouseLeaveCounter,disabled:s})):void 0;let K;if(C){const v=this.selectedOptions.length-f;v>0&&(K=l("div",{class:`${n}-base-selection-tag-wrapper`,key:"__counter__"},l(Mt,{size:a,ref:"counterRef",onMouseenter:this.handleMouseEnterCounter,disabled:s},{default:()=>`+${v}`})))}const he=k?d?l(Yt,{ref:"overflowRef",updateCounter:this.updateCounter,getCounter:this.getCounter,getTail:this.getTail,style:{width:"100%",display:"flex",overflow:"hidden"}},{default:N,counter:Y,tail:()=>te}):l(Yt,{ref:"overflowRef",updateCounter:this.updateCounter,getCounter:this.getCounter,style:{width:"100%",display:"flex",overflow:"hidden"}},{default:N,counter:Y}):C&&K?N().concat(K):N(),se=h?()=>l("div",{class:`${n}-base-selection-popover`},k?N():this.selectedOptions.map(E)):void 0,ve=h?Object.assign({show:this.showTagsPanel,trigger:"hover",overlap:!0,placement:"top",width:"trigger",onUpdateShow:this.onPopoverUpdateShow,theme:this.mergedTheme.peers.Popover,themeOverrides:this.mergedTheme.peerOverrides.Popover},b):null,le=(this.selected?!1:this.active?!this.pattern&&!this.isComposing:!0)?l("div",{class:`${n}-base-selection-placeholder ${n}-base-selection-overlay`},l("div",{class:`${n}-base-selection-placeholder__inner`},this.placeholder)):null,de=d?l("div",{ref:"patternInputWrapperRef",class:`${n}-base-selection-tags`},he,k?null:te,F):l("div",{ref:"multipleElRef",class:`${n}-base-selection-tags`,tabindex:s?void 0:0},he,F);D=l(un,null,h?l(Ro,Object.assign({},ve,{scrollable:!0,style:"max-height: calc(var(--v-target-height) * 6.6);"}),{trigger:()=>de,default:se}):de,le)}else if(d){const I=this.pattern||this.isComposing,E=this.active?!I:!this.selected,N=this.active?!1:this.selected;D=l("div",{ref:"patternInputWrapperRef",class:`${n}-base-selection-label`,title:this.patternInputFocused?void 0:Xt(this.label)},l("input",Object.assign({},this.inputProps,{ref:"patternInputRef",class:`${n}-base-selection-input`,value:this.active?this.pattern:"",placeholder:"",readonly:s,disabled:s,tabindex:-1,autofocus:this.autofocus,onFocus:this.handlePatternInputFocus,onBlur:this.handlePatternInputBlur,onInput:this.handlePatternInputInput,onCompositionstart:this.handleCompositionStart,onCompositionend:this.handleCompositionEnd})),N?l("div",{class:`${n}-base-selection-label__render-label ${n}-base-selection-overlay`,key:"input"},l("div",{class:`${n}-base-selection-overlay__wrapper`},O?O({option:this.selectedOption,handleClose:()=>{}}):y?y(this.selectedOption,!0):tt(this.label,this.selectedOption,!0))):null,E?l("div",{class:`${n}-base-selection-placeholder ${n}-base-selection-overlay`,key:"placeholder"},l("div",{class:`${n}-base-selection-overlay__wrapper`},this.filterablePlaceholder)):null,F)}else D=l("div",{ref:"singleElRef",class:`${n}-base-selection-label`,tabindex:this.disabled?void 0:0},this.label!==void 0?l("div",{class:`${n}-base-selection-input`,title:Xt(this.label),key:"input"},l("div",{class:`${n}-base-selection-input__content`},O?O({option:this.selectedOption,handleClose:()=>{}}):y?y(this.selectedOption,!0):tt(this.label,this.selectedOption,!0))):l("div",{class:`${n}-base-selection-placeholder ${n}-base-selection-overlay`,key:"placeholder"},l("div",{class:`${n}-base-selection-placeholder__inner`},this.placeholder)),F);return l("div",{ref:"selfRef",class:[`${n}-base-selection`,this.rtlEnabled&&`${n}-base-selection--rtl`,this.themeClass,e&&`${n}-base-selection--${e}-status`,{[`${n}-base-selection--active`]:this.active,[`${n}-base-selection--selected`]:this.selected||this.active&&this.pattern,[`${n}-base-selection--disabled`]:this.disabled,[`${n}-base-selection--multiple`]:this.multiple,[`${n}-base-selection--focus`]:this.focused}],style:this.cssVars,onClick:this.onClick,onMouseenter:this.handleMouseEnter,onMouseleave:this.handleMouseLeave,onKeydown:this.onKeydown,onFocusin:this.handleFocusin,onFocusout:this.handleFocusout,onMousedown:this.handleMouseDown},D,g?l("div",{class:`${n}-base-selection__border`}):null,g?l("div",{class:`${n}-base-selection__state-border`}):null)}}),bn=so("n-input"),Go=P("input",`
 max-width: 100%;
 cursor: text;
 line-height: 1.5;
 z-index: auto;
 outline: none;
 box-sizing: border-box;
 position: relative;
 display: inline-flex;
 border-radius: var(--n-border-radius);
 background-color: var(--n-color);
 transition: background-color .3s var(--n-bezier);
 font-size: var(--n-font-size);
 font-weight: var(--n-font-weight);
 --n-padding-vertical: calc((var(--n-height) - 1.5 * var(--n-font-size)) / 2);
`,[x("input, textarea",`
 overflow: hidden;
 flex-grow: 1;
 position: relative;
 `),x("input-el, textarea-el, input-mirror, textarea-mirror, separator, placeholder",`
 box-sizing: border-box;
 font-size: inherit;
 line-height: 1.5;
 font-family: inherit;
 border: none;
 outline: none;
 background-color: #0000;
 text-align: inherit;
 transition:
 -webkit-text-fill-color .3s var(--n-bezier),
 caret-color .3s var(--n-bezier),
 color .3s var(--n-bezier),
 text-decoration-color .3s var(--n-bezier);
 `),x("input-el, textarea-el",`
 -webkit-appearance: none;
 scrollbar-width: none;
 width: 100%;
 min-width: 0;
 text-decoration-color: var(--n-text-decoration-color);
 color: var(--n-text-color);
 caret-color: var(--n-caret-color);
 background-color: transparent;
 `,[ee("&::-webkit-scrollbar, &::-webkit-scrollbar-track-piece, &::-webkit-scrollbar-thumb",`
 width: 0;
 height: 0;
 display: none;
 `),ee("&::placeholder",`
 color: #0000;
 -webkit-text-fill-color: transparent !important;
 `),ee("&:-webkit-autofill ~",[x("placeholder","display: none;")])]),oe("round",[Ee("textarea","border-radius: calc(var(--n-height) / 2);")]),x("placeholder",`
 pointer-events: none;
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 overflow: hidden;
 color: var(--n-placeholder-color);
 `,[ee("span",`
 width: 100%;
 display: inline-block;
 `)]),oe("textarea",[x("placeholder","overflow: visible;")]),Ee("autosize","width: 100%;"),oe("autosize",[x("textarea-el, input-el",`
 position: absolute;
 top: 0;
 left: 0;
 height: 100%;
 `)]),P("input-wrapper",`
 overflow: hidden;
 display: inline-flex;
 flex-grow: 1;
 position: relative;
 padding-left: var(--n-padding-left);
 padding-right: var(--n-padding-right);
 `),x("input-mirror",`
 padding: 0;
 height: var(--n-height);
 line-height: var(--n-height);
 overflow: hidden;
 visibility: hidden;
 position: static;
 white-space: pre;
 pointer-events: none;
 `),x("input-el",`
 padding: 0;
 height: var(--n-height);
 line-height: var(--n-height);
 `,[ee("&[type=password]::-ms-reveal","display: none;"),ee("+",[x("placeholder",`
 display: flex;
 align-items: center; 
 `)])]),Ee("textarea",[x("placeholder","white-space: nowrap;")]),x("eye",`
 display: flex;
 align-items: center;
 justify-content: center;
 transition: color .3s var(--n-bezier);
 `),oe("textarea","width: 100%;",[P("input-word-count",`
 position: absolute;
 right: var(--n-padding-right);
 bottom: var(--n-padding-vertical);
 `),oe("resizable",[P("input-wrapper",`
 resize: vertical;
 min-height: var(--n-height);
 `)]),x("textarea-el, textarea-mirror, placeholder",`
 height: 100%;
 padding-left: 0;
 padding-right: 0;
 padding-top: var(--n-padding-vertical);
 padding-bottom: var(--n-padding-vertical);
 word-break: break-word;
 display: inline-block;
 vertical-align: bottom;
 box-sizing: border-box;
 line-height: var(--n-line-height-textarea);
 margin: 0;
 resize: none;
 white-space: pre-wrap;
 scroll-padding-block-end: var(--n-padding-vertical);
 `),x("textarea-mirror",`
 width: 100%;
 pointer-events: none;
 overflow: hidden;
 visibility: hidden;
 position: static;
 white-space: pre-wrap;
 overflow-wrap: break-word;
 `)]),oe("pair",[x("input-el, placeholder","text-align: center;"),x("separator",`
 display: flex;
 align-items: center;
 transition: color .3s var(--n-bezier);
 color: var(--n-text-color);
 white-space: nowrap;
 `,[P("icon",`
 color: var(--n-icon-color);
 `),P("base-icon",`
 color: var(--n-icon-color);
 `)])]),oe("disabled",`
 cursor: not-allowed;
 background-color: var(--n-color-disabled);
 `,[x("border","border: var(--n-border-disabled);"),x("input-el, textarea-el",`
 cursor: not-allowed;
 color: var(--n-text-color-disabled);
 text-decoration-color: var(--n-text-color-disabled);
 `),x("placeholder","color: var(--n-placeholder-color-disabled);"),x("separator","color: var(--n-text-color-disabled);",[P("icon",`
 color: var(--n-icon-color-disabled);
 `),P("base-icon",`
 color: var(--n-icon-color-disabled);
 `)]),P("input-word-count",`
 color: var(--n-count-text-color-disabled);
 `),x("suffix, prefix","color: var(--n-text-color-disabled);",[P("icon",`
 color: var(--n-icon-color-disabled);
 `),P("internal-icon",`
 color: var(--n-icon-color-disabled);
 `)])]),Ee("disabled",[x("eye",`
 color: var(--n-icon-color);
 cursor: pointer;
 `,[ee("&:hover",`
 color: var(--n-icon-color-hover);
 `),ee("&:active",`
 color: var(--n-icon-color-pressed);
 `)]),ee("&:hover",[x("state-border","border: var(--n-border-hover);")]),oe("focus","background-color: var(--n-color-focus);",[x("state-border",`
 border: var(--n-border-focus);
 box-shadow: var(--n-box-shadow-focus);
 `)])]),x("border, state-border",`
 box-sizing: border-box;
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 pointer-events: none;
 border-radius: inherit;
 border: var(--n-border);
 transition:
 box-shadow .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 `),x("state-border",`
 border-color: #0000;
 z-index: 1;
 `),x("prefix","margin-right: 4px;"),x("suffix",`
 margin-left: 4px;
 `),x("suffix, prefix",`
 transition: color .3s var(--n-bezier);
 flex-wrap: nowrap;
 flex-shrink: 0;
 line-height: var(--n-height);
 white-space: nowrap;
 display: inline-flex;
 align-items: center;
 justify-content: center;
 color: var(--n-suffix-text-color);
 `,[P("base-loading",`
 font-size: var(--n-icon-size);
 margin: 0 2px;
 color: var(--n-loading-color);
 `),P("base-clear",`
 font-size: var(--n-icon-size);
 `,[x("placeholder",[P("base-icon",`
 transition: color .3s var(--n-bezier);
 color: var(--n-icon-color);
 font-size: var(--n-icon-size);
 `)])]),ee(">",[P("icon",`
 transition: color .3s var(--n-bezier);
 color: var(--n-icon-color);
 font-size: var(--n-icon-size);
 `)]),P("base-icon",`
 font-size: var(--n-icon-size);
 `)]),P("input-word-count",`
 pointer-events: none;
 line-height: 1.5;
 font-size: .85em;
 color: var(--n-count-text-color);
 transition: color .3s var(--n-bezier);
 margin-left: 4px;
 font-variant: tabular-nums;
 `),["warning","error"].map(e=>oe(`${e}-status`,[Ee("disabled",[P("base-loading",`
 color: var(--n-loading-color-${e})
 `),x("input-el, textarea-el",`
 caret-color: var(--n-caret-color-${e});
 `),x("state-border",`
 border: var(--n-border-${e});
 `),ee("&:hover",[x("state-border",`
 border: var(--n-border-hover-${e});
 `)]),ee("&:focus",`
 background-color: var(--n-color-focus-${e});
 `,[x("state-border",`
 box-shadow: var(--n-box-shadow-focus-${e});
 border: var(--n-border-focus-${e});
 `)]),oe("focus",`
 background-color: var(--n-color-focus-${e});
 `,[x("state-border",`
 box-shadow: var(--n-box-shadow-focus-${e});
 border: var(--n-border-focus-${e});
 `)])])]))]),qo=P("input",[oe("disabled",[x("input-el, textarea-el",`
 -webkit-text-fill-color: var(--n-text-color-disabled);
 `)])]);function Yo(e){let r=0;for(const a of e)r++;return r}function bt(e){return e===""||e==null}function Xo(e){const r=T(null);function a(){const{value:f}=e;if(!(f!=null&&f.focus)){d();return}const{selectionStart:g,selectionEnd:n,value:b}=f;if(g==null||n==null){d();return}r.value={start:g,end:n,beforeText:b.slice(0,g),afterText:b.slice(n)}}function s(){var f;const{value:g}=r,{value:n}=e;if(!g||!n)return;const{value:b}=n,{start:R,beforeText:O,afterText:y}=g;let k=b.length;if(b.endsWith(y))k=b.length-y.length;else if(b.startsWith(O))k=O.length;else{const C=O[R-1],h=b.indexOf(C,R-1);h!==-1&&(k=h+1)}(f=n.setSelectionRange)===null||f===void 0||f.call(n,k,k)}function d(){r.value=null}return Fe(e,d),{recordCursor:a,restoreCursor:s}}const nn=fe({name:"InputWordCount",setup(e,{slots:r}){const{mergedValueRef:a,maxlengthRef:s,mergedClsPrefixRef:d,countGraphemesRef:f}=yt(bn),g=$(()=>{const{value:n}=a;return n===null||Array.isArray(n)?0:(f.value||Yo)(n)});return()=>{const{value:n}=s,{value:b}=a;return l("span",{class:`${d.value}-input-word-count`},uo(r.default,{value:b===null||Array.isArray(b)?"":b},()=>[n===void 0?g.value:`${g.value} / ${n}`]))}}}),Zo=Object.assign(Object.assign({},Le.props),{bordered:{type:Boolean,default:void 0},type:{type:String,default:"text"},placeholder:[Array,String],defaultValue:{type:[String,Array],default:null},value:[String,Array],disabled:{type:Boolean,default:void 0},size:String,rows:{type:[Number,String],default:3},round:Boolean,minlength:[String,Number],maxlength:[String,Number],clearable:Boolean,autosize:{type:[Boolean,Object],default:!1},pair:Boolean,separator:String,readonly:{type:[String,Boolean],default:!1},passivelyActivated:Boolean,showPasswordOn:String,stateful:{type:Boolean,default:!0},autofocus:Boolean,inputProps:Object,resizable:{type:Boolean,default:!0},showCount:Boolean,loading:{type:Boolean,default:void 0},allowInput:Function,renderCount:Function,onMousedown:Function,onKeydown:Function,onKeyup:[Function,Array],onInput:[Function,Array],onFocus:[Function,Array],onBlur:[Function,Array],onClick:[Function,Array],onChange:[Function,Array],onClear:[Function,Array],countGraphemes:Function,status:String,"onUpdate:value":[Function,Array],onUpdateValue:[Function,Array],textDecoration:[String,Array],attrSize:{type:Number,default:20},onInputBlur:[Function,Array],onInputFocus:[Function,Array],onDeactivate:[Function,Array],onActivate:[Function,Array],onWrapperFocus:[Function,Array],onWrapperBlur:[Function,Array],internalDeactivateOnEnter:Boolean,internalForceFocus:Boolean,internalLoadingBeforeSuffix:{type:Boolean,default:!0},showPasswordToggle:Boolean}),al=fe({name:"Input",props:Zo,slots:Object,setup(e){const{mergedClsPrefixRef:r,mergedBorderedRef:a,inlineThemeDisabled:s,mergedRtlRef:d,mergedComponentPropsRef:f}=Ct(e),g=Le("Input","-input",Go,ho,e,r);co&&ln("-input-safari",qo,r);const n=T(null),b=T(null),R=T(null),O=T(null),y=T(null),k=T(null),C=T(null),h=Xo(C),F=T(null),{localeRef:D}=fn("Input"),I=T(e.defaultValue),E=ae(e,"value"),N=Wt(E,I),te=cn(e,{mergedSize:t=>{var i,S;const{size:U}=e;if(U)return U;const{mergedSize:G}=t||{};if(G!=null&&G.value)return G.value;const H=(S=(i=f==null?void 0:f.value)===null||i===void 0?void 0:i.Input)===null||S===void 0?void 0:S.size;return H||"medium"}}),{mergedSizeRef:Y,mergedDisabledRef:K,mergedStatusRef:he}=te,se=T(!1),ve=T(!1),ue=T(!1),le=T(!1);let de=null;const v=$(()=>{const{placeholder:t,pair:i}=e;return i?Array.isArray(t)?t:t===void 0?["",""]:[t,t]:t===void 0?[D.value.placeholder]:[t]}),z=$(()=>{const{value:t}=ue,{value:i}=N,{value:S}=v;return!t&&(bt(i)||Array.isArray(i)&&bt(i[0]))&&S[0]}),V=$(()=>{const{value:t}=ue,{value:i}=N,{value:S}=v;return!t&&S[1]&&(bt(i)||Array.isArray(i)&&bt(i[1]))}),j=Ie(()=>e.internalForceFocus||se.value),q=Ie(()=>{if(K.value||e.readonly||!e.clearable||!j.value&&!ve.value)return!1;const{value:t}=N,{value:i}=j;return e.pair?!!(Array.isArray(t)&&(t[0]||t[1]))&&(ve.value||i):!!t&&(ve.value||i)}),Q=$(()=>{const{showPasswordOn:t}=e;if(t)return t;if(e.showPasswordToggle)return"click"}),W=T(!1),ne=$(()=>{const{textDecoration:t}=e;return t?Array.isArray(t)?t.map(i=>({textDecoration:i})):[{textDecoration:t}]:["",""]}),J=T(void 0),ce=()=>{var t,i;if(e.type==="textarea"){const{autosize:S}=e;if(S&&(J.value=(i=(t=F.value)===null||t===void 0?void 0:t.$el)===null||i===void 0?void 0:i.offsetWidth),!b.value||typeof S=="boolean")return;const{paddingTop:U,paddingBottom:G,lineHeight:H}=window.getComputedStyle(b.value),_e=Number(U.slice(0,-2)),Pe=Number(G.slice(0,-2)),Oe=Number(H.slice(0,-2)),{value:He}=R;if(!He)return;if(S.minRows){const Ke=Math.max(S.minRows,1),dt=`${_e+Pe+Oe*Ke}px`;He.style.minHeight=dt}if(S.maxRows){const Ke=`${_e+Pe+Oe*S.maxRows}px`;He.style.maxHeight=Ke}}},ge=$(()=>{const{maxlength:t}=e;return t===void 0?void 0:Number(t)});ht(()=>{const{value:t}=N;Array.isArray(t)||be(t)});const u=fo().proxy;function m(t,i){const{onUpdateValue:S,"onUpdate:value":U,onInput:G}=e,{nTriggerFormInput:H}=te;S&&Z(S,t,i),U&&Z(U,t,i),G&&Z(G,t,i),I.value=t,H()}function X(t,i){const{onChange:S}=e,{nTriggerFormChange:U}=te;S&&Z(S,t,i),I.value=t,U()}function we(t){const{onBlur:i}=e,{nTriggerFormBlur:S}=te;i&&Z(i,t),S()}function ke(t){const{onFocus:i}=e,{nTriggerFormFocus:S}=te;i&&Z(i,t),S()}function xe(t){const{onClear:i}=e;i&&Z(i,t)}function pe(t){const{onInputBlur:i}=e;i&&Z(i,t)}function Me(t){const{onInputFocus:i}=e;i&&Z(i,t)}function ye(){const{onDeactivate:t}=e;t&&Z(t)}function De(){const{onActivate:t}=e;t&&Z(t)}function Ve(t){const{onClick:i}=e;i&&Z(i,t)}function We(t){const{onWrapperFocus:i}=e;i&&Z(i,t)}function ze(t){const{onWrapperBlur:i}=e;i&&Z(i,t)}function Te(){ue.value=!0}function Ne(t){ue.value=!1,t.target===k.value?Ce(t,1):Ce(t,0)}function Ce(t,i=0,S="input"){const U=t.target.value;if(be(U),t instanceof InputEvent&&!t.isComposing&&(ue.value=!1),e.type==="textarea"){const{value:H}=F;H&&H.syncUnifiedContainer()}if(de=U,ue.value)return;h.recordCursor();const G=je(U);if(G)if(!e.pair)S==="input"?m(U,{source:i}):X(U,{source:i});else{let{value:H}=N;Array.isArray(H)?H=[H[0],H[1]]:H=["",""],H[i]=U,S==="input"?m(H,{source:i}):X(H,{source:i})}u.$forceUpdate(),G||wt(h.restoreCursor)}function je(t){const{countGraphemes:i,maxlength:S,minlength:U}=e;if(i){let H;if(S!==void 0&&(H===void 0&&(H=i(t)),H>Number(S))||U!==void 0&&(H===void 0&&(H=i(t)),H<Number(S)))return!1}const{allowInput:G}=e;return typeof G=="function"?G(t):!0}function Be(t){pe(t),t.relatedTarget===n.value&&ye(),t.relatedTarget!==null&&(t.relatedTarget===y.value||t.relatedTarget===k.value||t.relatedTarget===b.value)||(le.value=!1),w(t,"blur"),C.value=null}function $e(t,i){Me(t),se.value=!0,le.value=!0,De(),w(t,"focus"),i===0?C.value=y.value:i===1?C.value=k.value:i===2&&(C.value=b.value)}function me(t){e.passivelyActivated&&(ze(t),w(t,"blur"))}function c(t){e.passivelyActivated&&(se.value=!0,We(t),w(t,"focus"))}function w(t,i){t.relatedTarget!==null&&(t.relatedTarget===y.value||t.relatedTarget===k.value||t.relatedTarget===b.value||t.relatedTarget===n.value)||(i==="focus"?(ke(t),se.value=!0):i==="blur"&&(we(t),se.value=!1))}function re(t,i){Ce(t,i,"change")}function rt(t){Ve(t)}function it(t){xe(t),Ue()}function Ue(){e.pair?(m(["",""],{source:"clear"}),X(["",""],{source:"clear"})):(m("",{source:"clear"}),X("",{source:"clear"}))}function Ge(t){const{onMousedown:i}=e;i&&i(t);const{tagName:S}=t.target;if(S!=="INPUT"&&S!=="TEXTAREA"){if(e.resizable){const{value:U}=n;if(U){const{left:G,top:H,width:_e,height:Pe}=U.getBoundingClientRect(),Oe=14;if(G+_e-Oe<t.clientX&&t.clientX<G+_e&&H+Pe-Oe<t.clientY&&t.clientY<H+Pe)return}}t.preventDefault(),se.value||_()}}function qe(){var t;ve.value=!0,e.type==="textarea"&&((t=F.value)===null||t===void 0||t.handleMouseEnterWrapper())}function Ye(){var t;ve.value=!1,e.type==="textarea"&&((t=F.value)===null||t===void 0||t.handleMouseLeaveWrapper())}function at(){K.value||Q.value==="click"&&(W.value=!W.value)}function st(t){if(K.value)return;t.preventDefault();const i=U=>{U.preventDefault(),Gt("mouseup",document,i)};if(Ut("mouseup",document,i),Q.value!=="mousedown")return;W.value=!0;const S=()=>{W.value=!1,Gt("mouseup",document,S)};Ut("mouseup",document,S)}function Xe(t){e.onKeyup&&Z(e.onKeyup,t)}function Se(t){switch(e.onKeydown&&Z(e.onKeydown,t),t.key){case"Escape":p();break;case"Enter":o(t);break}}function o(t){var i,S;if(e.passivelyActivated){const{value:U}=le;if(U){e.internalDeactivateOnEnter&&p();return}t.preventDefault(),e.type==="textarea"?(i=b.value)===null||i===void 0||i.focus():(S=y.value)===null||S===void 0||S.focus()}}function p(){e.passivelyActivated&&(le.value=!1,wt(()=>{var t;(t=n.value)===null||t===void 0||t.focus()}))}function _(){var t,i,S;K.value||(e.passivelyActivated?(t=n.value)===null||t===void 0||t.focus():((i=b.value)===null||i===void 0||i.focus(),(S=y.value)===null||S===void 0||S.focus()))}function A(){var t;!((t=n.value)===null||t===void 0)&&t.contains(document.activeElement)&&document.activeElement.blur()}function M(){var t,i;(t=b.value)===null||t===void 0||t.select(),(i=y.value)===null||i===void 0||i.select()}function B(){K.value||(b.value?b.value.focus():y.value&&y.value.focus())}function L(){const{value:t}=n;t!=null&&t.contains(document.activeElement)&&t!==document.activeElement&&p()}function ie(t){if(e.type==="textarea"){const{value:i}=b;i==null||i.scrollTo(t)}else{const{value:i}=y;i==null||i.scrollTo(t)}}function be(t){const{type:i,pair:S,autosize:U}=e;if(!S&&U)if(i==="textarea"){const{value:G}=R;G&&(G.textContent=`${t??""}\r
`)}else{const{value:G}=O;G&&(t?G.textContent=t:G.innerHTML="&nbsp;")}}function Rt(){ce()}const vt=T({top:"0"});function Ft(t){var i;const{scrollTop:S}=t.target;vt.value.top=`${-S}px`,(i=F.value)===null||i===void 0||i.syncUnifiedContainer()}let Ze=null;Vt(()=>{const{autosize:t,type:i}=e;t&&i==="textarea"?Ze=Fe(N,S=>{!Array.isArray(S)&&S!==de&&be(S)}):Ze==null||Ze()});let Qe=null;Vt(()=>{e.type==="textarea"?Qe=Fe(N,t=>{var i;!Array.isArray(t)&&t!==de&&((i=F.value)===null||i===void 0||i.syncUnifiedContainer())}):Qe==null||Qe()}),mt(bn,{mergedValueRef:N,maxlengthRef:ge,mergedClsPrefixRef:r,countGraphemesRef:ae(e,"countGraphemes")});const zt={wrapperElRef:n,inputElRef:y,textareaElRef:b,isCompositing:ue,clear:Ue,focus:_,blur:A,select:M,deactivate:L,activate:B,scrollTo:ie},Tt=Ht("Input",d,r),gt=$(()=>{const{value:t}=Y,{common:{cubicBezierEaseInOut:i},self:{color:S,borderRadius:U,textColor:G,caretColor:H,caretColorError:_e,caretColorWarning:Pe,textDecorationColor:Oe,border:He,borderDisabled:Ke,borderHover:dt,borderFocus:_t,placeholderColor:Pt,placeholderColorDisabled:Ot,lineHeightTextarea:It,colorDisabled:Je,colorFocus:et,textColorDisabled:wn,boxShadowFocus:xn,iconSize:yn,colorFocusWarning:Cn,boxShadowFocusWarning:Sn,borderWarning:Rn,borderFocusWarning:Fn,borderHoverWarning:zn,colorFocusError:Tn,boxShadowFocusError:_n,borderError:Pn,borderFocusError:On,borderHoverError:In,clearSize:kn,clearColor:Mn,clearColorHover:Bn,clearColorPressed:$n,iconColor:An,iconColorDisabled:En,suffixTextColor:Ln,countTextColor:Dn,countTextColorDisabled:Vn,iconColorHover:Wn,iconColorPressed:Nn,loadingColor:jn,loadingColorError:Hn,loadingColorWarning:Kn,fontWeight:Un,[Re("padding",t)]:Gn,[Re("fontSize",t)]:qn,[Re("height",t)]:Yn}}=g.value,{left:Xn,right:Zn}=lt(Gn);return{"--n-bezier":i,"--n-count-text-color":Dn,"--n-count-text-color-disabled":Vn,"--n-color":S,"--n-font-size":qn,"--n-font-weight":Un,"--n-border-radius":U,"--n-height":Yn,"--n-padding-left":Xn,"--n-padding-right":Zn,"--n-text-color":G,"--n-caret-color":H,"--n-text-decoration-color":Oe,"--n-border":He,"--n-border-disabled":Ke,"--n-border-hover":dt,"--n-border-focus":_t,"--n-placeholder-color":Pt,"--n-placeholder-color-disabled":Ot,"--n-icon-size":yn,"--n-line-height-textarea":It,"--n-color-disabled":Je,"--n-color-focus":et,"--n-text-color-disabled":wn,"--n-box-shadow-focus":xn,"--n-loading-color":jn,"--n-caret-color-warning":Pe,"--n-color-focus-warning":Cn,"--n-box-shadow-focus-warning":Sn,"--n-border-warning":Rn,"--n-border-focus-warning":Fn,"--n-border-hover-warning":zn,"--n-loading-color-warning":Kn,"--n-caret-color-error":_e,"--n-color-focus-error":Tn,"--n-box-shadow-focus-error":_n,"--n-border-error":Pn,"--n-border-focus-error":On,"--n-border-hover-error":In,"--n-loading-color-error":Hn,"--n-clear-color":Mn,"--n-clear-size":kn,"--n-clear-color-hover":Bn,"--n-clear-color-pressed":$n,"--n-icon-color":An,"--n-icon-color-hover":Wn,"--n-icon-color-pressed":Nn,"--n-icon-color-disabled":En,"--n-suffix-text-color":Ln}}),Ae=s?St("input",$(()=>{const{value:t}=Y;return t[0]}),gt,e):void 0;return Object.assign(Object.assign({},zt),{wrapperElRef:n,inputElRef:y,inputMirrorElRef:O,inputEl2Ref:k,textareaElRef:b,textareaMirrorElRef:R,textareaScrollbarInstRef:F,rtlEnabled:Tt,uncontrolledValue:I,mergedValue:N,passwordVisible:W,mergedPlaceholder:v,showPlaceholder1:z,showPlaceholder2:V,mergedFocus:j,isComposing:ue,activated:le,showClearButton:q,mergedSize:Y,mergedDisabled:K,textDecorationStyle:ne,mergedClsPrefix:r,mergedBordered:a,mergedShowPasswordOn:Q,placeholderStyle:vt,mergedStatus:he,textAreaScrollContainerWidth:J,handleTextAreaScroll:Ft,handleCompositionStart:Te,handleCompositionEnd:Ne,handleInput:Ce,handleInputBlur:Be,handleInputFocus:$e,handleWrapperBlur:me,handleWrapperFocus:c,handleMouseEnter:qe,handleMouseLeave:Ye,handleMouseDown:Ge,handleChange:re,handleClick:rt,handleClear:it,handlePasswordToggleClick:at,handlePasswordToggleMousedown:st,handleWrapperKeydown:Se,handleWrapperKeyup:Xe,handleTextAreaMirrorResize:Rt,getTextareaScrollContainer:()=>b.value,mergedTheme:g,cssVars:s?void 0:gt,themeClass:Ae==null?void 0:Ae.themeClass,onRender:Ae==null?void 0:Ae.onRender})},render(){var e,r,a,s,d,f,g;const{mergedClsPrefix:n,mergedStatus:b,themeClass:R,type:O,countGraphemes:y,onRender:k}=this,C=this.$slots;return k==null||k(),l("div",{ref:"wrapperElRef",class:[`${n}-input`,`${n}-input--${this.mergedSize}-size`,R,b&&`${n}-input--${b}-status`,{[`${n}-input--rtl`]:this.rtlEnabled,[`${n}-input--disabled`]:this.mergedDisabled,[`${n}-input--textarea`]:O==="textarea",[`${n}-input--resizable`]:this.resizable&&!this.autosize,[`${n}-input--autosize`]:this.autosize,[`${n}-input--round`]:this.round&&O!=="textarea",[`${n}-input--pair`]:this.pair,[`${n}-input--focus`]:this.mergedFocus,[`${n}-input--stateful`]:this.stateful}],style:this.cssVars,tabindex:!this.mergedDisabled&&this.passivelyActivated&&!this.activated?0:void 0,onFocus:this.handleWrapperFocus,onBlur:this.handleWrapperBlur,onClick:this.handleClick,onMousedown:this.handleMouseDown,onMouseenter:this.handleMouseEnter,onMouseleave:this.handleMouseLeave,onCompositionstart:this.handleCompositionStart,onCompositionend:this.handleCompositionEnd,onKeyup:this.handleWrapperKeyup,onKeydown:this.handleWrapperKeydown},l("div",{class:`${n}-input-wrapper`},nt(C.prefix,h=>h&&l("div",{class:`${n}-input__prefix`},h)),O==="textarea"?l(dn,{ref:"textareaScrollbarInstRef",class:`${n}-input__textarea`,container:this.getTextareaScrollContainer,theme:(r=(e=this.theme)===null||e===void 0?void 0:e.peers)===null||r===void 0?void 0:r.Scrollbar,themeOverrides:(s=(a=this.themeOverrides)===null||a===void 0?void 0:a.peers)===null||s===void 0?void 0:s.Scrollbar,triggerDisplayManually:!0,useUnifiedContainer:!0,internalHoistYRail:!0},{default:()=>{var h,F;const{textAreaScrollContainerWidth:D}=this,I={width:this.autosize&&D&&`${D}px`};return l(un,null,l("textarea",Object.assign({},this.inputProps,{ref:"textareaElRef",class:[`${n}-input__textarea-el`,(h=this.inputProps)===null||h===void 0?void 0:h.class],autofocus:this.autofocus,rows:Number(this.rows),placeholder:this.placeholder,value:this.mergedValue,disabled:this.mergedDisabled,maxlength:y?void 0:this.maxlength,minlength:y?void 0:this.minlength,readonly:this.readonly,tabindex:this.passivelyActivated&&!this.activated?-1:void 0,style:[this.textDecorationStyle[0],(F=this.inputProps)===null||F===void 0?void 0:F.style,I],onBlur:this.handleInputBlur,onFocus:E=>{this.handleInputFocus(E,2)},onInput:this.handleInput,onChange:this.handleChange,onScroll:this.handleTextAreaScroll})),this.showPlaceholder1?l("div",{class:`${n}-input__placeholder`,style:[this.placeholderStyle,I],key:"placeholder"},this.mergedPlaceholder[0]):null,this.autosize?l(Lt,{onResize:this.handleTextAreaMirrorResize},{default:()=>l("div",{ref:"textareaMirrorElRef",class:`${n}-input__textarea-mirror`,key:"mirror"})}):null)}}):l("div",{class:`${n}-input__input`},l("input",Object.assign({type:O==="password"&&this.mergedShowPasswordOn&&this.passwordVisible?"text":O},this.inputProps,{ref:"inputElRef",class:[`${n}-input__input-el`,(d=this.inputProps)===null||d===void 0?void 0:d.class],style:[this.textDecorationStyle[0],(f=this.inputProps)===null||f===void 0?void 0:f.style],tabindex:this.passivelyActivated&&!this.activated?-1:(g=this.inputProps)===null||g===void 0?void 0:g.tabindex,placeholder:this.mergedPlaceholder[0],disabled:this.mergedDisabled,maxlength:y?void 0:this.maxlength,minlength:y?void 0:this.minlength,value:Array.isArray(this.mergedValue)?this.mergedValue[0]:this.mergedValue,readonly:this.readonly,autofocus:this.autofocus,size:this.attrSize,onBlur:this.handleInputBlur,onFocus:h=>{this.handleInputFocus(h,0)},onInput:h=>{this.handleInput(h,0)},onChange:h=>{this.handleChange(h,0)}})),this.showPlaceholder1?l("div",{class:`${n}-input__placeholder`},l("span",null,this.mergedPlaceholder[0])):null,this.autosize?l("div",{class:`${n}-input__input-mirror`,key:"mirror",ref:"inputMirrorElRef"}," "):null),!this.pair&&nt(C.suffix,h=>h||this.clearable||this.showCount||this.mergedShowPasswordOn||this.loading!==void 0?l("div",{class:`${n}-input__suffix`},[nt(C["clear-icon-placeholder"],F=>(this.clearable||F)&&l(jt,{clsPrefix:n,show:this.showClearButton,onClear:this.handleClear},{placeholder:()=>F,icon:()=>{var D,I;return(I=(D=this.$slots)["clear-icon"])===null||I===void 0?void 0:I.call(D)}})),this.internalLoadingBeforeSuffix?null:h,this.loading!==void 0?l(pn,{clsPrefix:n,loading:this.loading,showArrow:!1,showClear:!1,style:this.cssVars}):null,this.internalLoadingBeforeSuffix?h:null,this.showCount&&this.type!=="textarea"?l(nn,null,{default:F=>{var D;const{renderCount:I}=this;return I?I(F):(D=C.count)===null||D===void 0?void 0:D.call(C,F)}}):null,this.mergedShowPasswordOn&&this.type==="password"?l("div",{class:`${n}-input__eye`,onMousedown:this.handlePasswordToggleMousedown,onClick:this.handlePasswordToggleClick},this.passwordVisible?ot(C["password-visible-icon"],()=>[l(ft,{clsPrefix:n},{default:()=>l(Lo,null)})]):ot(C["password-invisible-icon"],()=>[l(ft,{clsPrefix:n},{default:()=>l(Do,null)})])):null]):null)),this.pair?l("span",{class:`${n}-input__separator`},ot(C.separator,()=>[this.separator])):null,this.pair?l("div",{class:`${n}-input-wrapper`},l("div",{class:`${n}-input__input`},l("input",{ref:"inputEl2Ref",type:this.type,class:`${n}-input__input-el`,tabindex:this.passivelyActivated&&!this.activated?-1:void 0,placeholder:this.mergedPlaceholder[1],disabled:this.mergedDisabled,maxlength:y?void 0:this.maxlength,minlength:y?void 0:this.minlength,value:Array.isArray(this.mergedValue)?this.mergedValue[1]:void 0,readonly:this.readonly,style:this.textDecorationStyle[1],onBlur:this.handleInputBlur,onFocus:h=>{this.handleInputFocus(h,1)},onInput:h=>{this.handleInput(h,1)},onChange:h=>{this.handleChange(h,1)}}),this.showPlaceholder2?l("div",{class:`${n}-input__placeholder`},l("span",null,this.mergedPlaceholder[1])):null),nt(C.suffix,h=>(this.clearable||h)&&l("div",{class:`${n}-input__suffix`},[this.clearable&&l(jt,{clsPrefix:n,show:this.showClearButton,onClear:this.handleClear},{icon:()=>{var F;return(F=C["clear-icon"])===null||F===void 0?void 0:F.call(C)},placeholder:()=>{var F;return(F=C["clear-icon-placeholder"])===null||F===void 0?void 0:F.call(C)}}),h]))):null,this.mergedBordered?l("div",{class:`${n}-input__border`}):null,this.mergedBordered?l("div",{class:`${n}-input__state-border`}):null,this.showCount&&O==="textarea"?l(nn,null,{default:h=>{var F;const{renderCount:D}=this;return D?D(h):(F=C.count)===null||F===void 0?void 0:F.call(C,h)}}):null)}});function xt(e){return e.type==="group"}function mn(e){return e.type==="ignored"}function Et(e,r){try{return!!(1+r.toString().toLowerCase().indexOf(e.trim().toLowerCase()))}catch{return!1}}function Qo(e,r){return{getIsGroup:xt,getIgnored:mn,getKey(s){return xt(s)?s.name||s.key||"key-required":s[e]},getChildren(s){return s[r]}}}function Jo(e,r,a,s){if(!r)return e;function d(f){if(!Array.isArray(f))return[];const g=[];for(const n of f)if(xt(n)){const b=d(n[s]);b.length&&g.push(Object.assign({},n,{[s]:b}))}else{if(mn(n))continue;r(a,n)&&g.push(n)}return g}return d(e)}function el(e,r,a){const s=new Map;return e.forEach(d=>{xt(d)?d[a].forEach(f=>{s.set(f[r],f)}):s.set(d[r],d)}),s}const tl=ee([P("select",`
 z-index: auto;
 outline: none;
 width: 100%;
 position: relative;
 font-weight: var(--n-font-weight);
 `),P("select-menu",`
 margin: 4px 0;
 box-shadow: var(--n-menu-box-shadow);
 `,[an({originalTransition:"background-color .3s var(--n-bezier), box-shadow .3s var(--n-bezier)"})])]),nl=Object.assign(Object.assign({},Le.props),{to:Nt.propTo,bordered:{type:Boolean,default:void 0},clearable:Boolean,clearCreatedOptionsOnClear:{type:Boolean,default:!0},clearFilterAfterSelect:{type:Boolean,default:!0},options:{type:Array,default:()=>[]},defaultValue:{type:[String,Number,Array],default:null},keyboard:{type:Boolean,default:!0},value:[String,Number,Array],placeholder:String,menuProps:Object,multiple:Boolean,size:String,menuSize:{type:String},filterable:Boolean,disabled:{type:Boolean,default:void 0},remote:Boolean,loading:Boolean,filter:Function,placement:{type:String,default:"bottom-start"},widthMode:{type:String,default:"trigger"},tag:Boolean,onCreate:Function,fallbackOption:{type:[Function,Boolean],default:void 0},show:{type:Boolean,default:void 0},showArrow:{type:Boolean,default:!0},maxTagCount:[Number,String],ellipsisTagPopoverProps:Object,consistentMenuWidth:{type:Boolean,default:!0},virtualScroll:{type:Boolean,default:!0},labelField:{type:String,default:"label"},valueField:{type:String,default:"value"},childrenField:{type:String,default:"children"},renderLabel:Function,renderOption:Function,renderTag:Function,"onUpdate:value":[Function,Array],inputProps:Object,nodeProps:Function,ignoreComposition:{type:Boolean,default:!0},showOnFocus:Boolean,onUpdateValue:[Function,Array],onBlur:[Function,Array],onClear:[Function,Array],onFocus:[Function,Array],onScroll:[Function,Array],onSearch:[Function,Array],onUpdateShow:[Function,Array],"onUpdate:show":[Function,Array],displayDirective:{type:String,default:"show"},resetMenuOnOptionsChange:{type:Boolean,default:!0},status:String,showCheckmark:{type:Boolean,default:!0},scrollbarProps:Object,onChange:[Function,Array],items:Array}),sl=fe({name:"Select",props:nl,slots:Object,setup(e){const{mergedClsPrefixRef:r,mergedBorderedRef:a,namespaceRef:s,inlineThemeDisabled:d,mergedComponentPropsRef:f}=Ct(e),g=Le("Select","-select",tl,wo,e,r),n=T(e.defaultValue),b=ae(e,"value"),R=Wt(b,n),O=T(!1),y=T(""),k=Oo(e,["items","options"]),C=T([]),h=T([]),F=$(()=>h.value.concat(C.value).concat(k.value)),D=$(()=>{const{filter:o}=e;if(o)return o;const{labelField:p,valueField:_}=e;return(A,M)=>{if(!M)return!1;const B=M[p];if(typeof B=="string")return Et(A,B);const L=M[_];return typeof L=="string"?Et(A,L):typeof L=="number"?Et(A,String(L)):!1}}),I=$(()=>{if(e.remote)return k.value;{const{value:o}=F,{value:p}=y;return!p.length||!e.filterable?o:Jo(o,D.value,p,e.childrenField)}}),E=$(()=>{const{valueField:o,childrenField:p}=e,_=Qo(o,p);return _o(I.value,_)}),N=$(()=>el(F.value,e.valueField,e.childrenField)),te=T(!1),Y=Wt(ae(e,"show"),te),K=T(null),he=T(null),se=T(null),{localeRef:ve}=fn("Select"),ue=$(()=>{var o;return(o=e.placeholder)!==null&&o!==void 0?o:ve.value.placeholder}),le=[],de=T(new Map),v=$(()=>{const{fallbackOption:o}=e;if(o===void 0){const{labelField:p,valueField:_}=e;return A=>({[p]:String(A),[_]:A})}return o===!1?!1:p=>Object.assign(o(p),{value:p})});function z(o){const p=e.remote,{value:_}=de,{value:A}=N,{value:M}=v,B=[];return o.forEach(L=>{if(A.has(L))B.push(A.get(L));else if(p&&_.has(L))B.push(_.get(L));else if(M){const ie=M(L);ie&&B.push(ie)}}),B}const V=$(()=>{if(e.multiple){const{value:o}=R;return Array.isArray(o)?z(o):[]}return null}),j=$(()=>{const{value:o}=R;return!e.multiple&&!Array.isArray(o)?o===null?null:z([o])[0]||null:null}),q=cn(e,{mergedSize:o=>{var p,_;const{size:A}=e;if(A)return A;const{mergedSize:M}=o||{};if(M!=null&&M.value)return M.value;const B=(_=(p=f==null?void 0:f.value)===null||p===void 0?void 0:p.Select)===null||_===void 0?void 0:_.size;return B||"medium"}}),{mergedSizeRef:Q,mergedDisabledRef:W,mergedStatusRef:ne}=q;function J(o,p){const{onChange:_,"onUpdate:value":A,onUpdateValue:M}=e,{nTriggerFormChange:B,nTriggerFormInput:L}=q;_&&Z(_,o,p),M&&Z(M,o,p),A&&Z(A,o,p),n.value=o,B(),L()}function ce(o){const{onBlur:p}=e,{nTriggerFormBlur:_}=q;p&&Z(p,o),_()}function ge(){const{onClear:o}=e;o&&Z(o)}function u(o){const{onFocus:p,showOnFocus:_}=e,{nTriggerFormFocus:A}=q;p&&Z(p,o),A(),_&&xe()}function m(o){const{onSearch:p}=e;p&&Z(p,o)}function X(o){const{onScroll:p}=e;p&&Z(p,o)}function we(){var o;const{remote:p,multiple:_}=e;if(p){const{value:A}=de;if(_){const{valueField:M}=e;(o=V.value)===null||o===void 0||o.forEach(B=>{A.set(B[M],B)})}else{const M=j.value;M&&A.set(M[e.valueField],M)}}}function ke(o){const{onUpdateShow:p,"onUpdate:show":_}=e;p&&Z(p,o),_&&Z(_,o),te.value=o}function xe(){W.value||(ke(!0),te.value=!0,e.filterable&&Ye())}function pe(){ke(!1)}function Me(){y.value="",h.value=le}const ye=T(!1);function De(){e.filterable&&(ye.value=!0)}function Ve(){e.filterable&&(ye.value=!1,Y.value||Me())}function We(){W.value||(Y.value?e.filterable?Ye():pe():xe())}function ze(o){var p,_;!((_=(p=se.value)===null||p===void 0?void 0:p.selfRef)===null||_===void 0)&&_.contains(o.relatedTarget)||(O.value=!1,ce(o),pe())}function Te(o){u(o),O.value=!0}function Ne(){O.value=!0}function Ce(o){var p;!((p=K.value)===null||p===void 0)&&p.$el.contains(o.relatedTarget)||(O.value=!1,ce(o),pe())}function je(){var o;(o=K.value)===null||o===void 0||o.focus(),pe()}function Be(o){var p;Y.value&&(!((p=K.value)===null||p===void 0)&&p.$el.contains(bo(o))||pe())}function $e(o){if(!Array.isArray(o))return[];if(v.value)return Array.from(o);{const{remote:p}=e,{value:_}=N;if(p){const{value:A}=de;return o.filter(M=>_.has(M)||A.has(M))}else return o.filter(A=>_.has(A))}}function me(o){c(o.rawNode)}function c(o){if(W.value)return;const{tag:p,remote:_,clearFilterAfterSelect:A,valueField:M}=e;if(p&&!_){const{value:B}=h,L=B[0]||null;if(L){const ie=C.value;ie.length?ie.push(L):C.value=[L],h.value=le}}if(_&&de.value.set(o[M],o),e.multiple){const B=$e(R.value),L=B.findIndex(ie=>ie===o[M]);if(~L){if(B.splice(L,1),p&&!_){const ie=w(o[M]);~ie&&(C.value.splice(ie,1),A&&(y.value=""))}}else B.push(o[M]),A&&(y.value="");J(B,z(B))}else{if(p&&!_){const B=w(o[M]);~B?C.value=[C.value[B]]:C.value=le}qe(),pe(),J(o[M],o)}}function w(o){return C.value.findIndex(_=>_[e.valueField]===o)}function re(o){Y.value||xe();const{value:p}=o.target;y.value=p;const{tag:_,remote:A}=e;if(m(p),_&&!A){if(!p){h.value=le;return}const{onCreate:M}=e,B=M?M(p):{[e.labelField]:p,[e.valueField]:p},{valueField:L,labelField:ie}=e;k.value.some(be=>be[L]===B[L]||be[ie]===B[ie])||C.value.some(be=>be[L]===B[L]||be[ie]===B[ie])?h.value=le:h.value=[B]}}function rt(o){o.stopPropagation();const{multiple:p,tag:_,remote:A,clearCreatedOptionsOnClear:M}=e;!p&&e.filterable&&pe(),_&&!A&&M&&(C.value=le),ge(),p?J([],[]):J(null,null)}function it(o){!ct(o,"action")&&!ct(o,"empty")&&!ct(o,"header")&&o.preventDefault()}function Ue(o){X(o)}function Ge(o){var p,_,A,M,B;if(!e.keyboard){o.preventDefault();return}switch(o.key){case" ":if(e.filterable)break;o.preventDefault();case"Enter":if(!(!((p=K.value)===null||p===void 0)&&p.isComposing)){if(Y.value){const L=(_=se.value)===null||_===void 0?void 0:_.getPendingTmNode();L?me(L):e.filterable||(pe(),qe())}else if(xe(),e.tag&&ye.value){const L=h.value[0];if(L){const ie=L[e.valueField],{value:be}=R;e.multiple&&Array.isArray(be)&&be.includes(ie)||c(L)}}}o.preventDefault();break;case"ArrowUp":if(o.preventDefault(),e.loading)return;Y.value&&((A=se.value)===null||A===void 0||A.prev());break;case"ArrowDown":if(o.preventDefault(),e.loading)return;Y.value?(M=se.value)===null||M===void 0||M.next():xe();break;case"Escape":Y.value&&(mo(o),pe()),(B=K.value)===null||B===void 0||B.focus();break}}function qe(){var o;(o=K.value)===null||o===void 0||o.focus()}function Ye(){var o;(o=K.value)===null||o===void 0||o.focusInput()}function at(){var o;Y.value&&((o=he.value)===null||o===void 0||o.syncPosition())}we(),Fe(ae(e,"options"),we);const st={focus:()=>{var o;(o=K.value)===null||o===void 0||o.focus()},focusInput:()=>{var o;(o=K.value)===null||o===void 0||o.focusInput()},blur:()=>{var o;(o=K.value)===null||o===void 0||o.blur()},blurInput:()=>{var o;(o=K.value)===null||o===void 0||o.blurInput()}},Xe=$(()=>{const{self:{menuBoxShadow:o}}=g.value;return{"--n-menu-box-shadow":o}}),Se=d?St("select",void 0,Xe,e):void 0;return Object.assign(Object.assign({},st),{mergedStatus:ne,mergedClsPrefix:r,mergedBordered:a,namespace:s,treeMate:E,isMounted:po(),triggerRef:K,menuRef:se,pattern:y,uncontrolledShow:te,mergedShow:Y,adjustedTo:Nt(e),uncontrolledValue:n,mergedValue:R,followerRef:he,localizedPlaceholder:ue,selectedOption:j,selectedOptions:V,mergedSize:Q,mergedDisabled:W,focused:O,activeWithoutMenuOpen:ye,inlineThemeDisabled:d,onTriggerInputFocus:De,onTriggerInputBlur:Ve,handleTriggerOrMenuResize:at,handleMenuFocus:Ne,handleMenuBlur:Ce,handleMenuTabOut:je,handleTriggerClick:We,handleToggle:me,handleDeleteOption:c,handlePatternInput:re,handleClear:rt,handleTriggerBlur:ze,handleTriggerFocus:Te,handleKeydown:Ge,handleMenuAfterLeave:Me,handleMenuClickOutside:Be,handleMenuScroll:Ue,handleMenuKeydown:Ge,handleMenuMousedown:it,mergedTheme:g,cssVars:d?void 0:Xe,themeClass:Se==null?void 0:Se.themeClass,onRender:Se==null?void 0:Se.onRender})},render(){return l("div",{class:`${this.mergedClsPrefix}-select`},l(Fo,null,{default:()=>[l(zo,null,{default:()=>l(Uo,{ref:"triggerRef",inlineThemeDisabled:this.inlineThemeDisabled,status:this.mergedStatus,inputProps:this.inputProps,clsPrefix:this.mergedClsPrefix,showArrow:this.showArrow,maxTagCount:this.maxTagCount,ellipsisTagPopoverProps:this.ellipsisTagPopoverProps,bordered:this.mergedBordered,active:this.activeWithoutMenuOpen||this.mergedShow,pattern:this.pattern,placeholder:this.localizedPlaceholder,selectedOption:this.selectedOption,selectedOptions:this.selectedOptions,multiple:this.multiple,renderTag:this.renderTag,renderLabel:this.renderLabel,filterable:this.filterable,clearable:this.clearable,disabled:this.mergedDisabled,size:this.mergedSize,theme:this.mergedTheme.peers.InternalSelection,labelField:this.labelField,valueField:this.valueField,themeOverrides:this.mergedTheme.peerOverrides.InternalSelection,loading:this.loading,focused:this.focused,onClick:this.handleTriggerClick,onDeleteOption:this.handleDeleteOption,onPatternInput:this.handlePatternInput,onClear:this.handleClear,onBlur:this.handleTriggerBlur,onFocus:this.handleTriggerFocus,onKeydown:this.handleKeydown,onPatternBlur:this.onTriggerInputBlur,onPatternFocus:this.onTriggerInputFocus,onResize:this.handleTriggerOrMenuResize,ignoreComposition:this.ignoreComposition},{arrow:()=>{var e,r;return[(r=(e=this.$slots).arrow)===null||r===void 0?void 0:r.call(e)]}})}),l(To,{ref:"followerRef",show:this.mergedShow,to:this.adjustedTo,teleportDisabled:this.adjustedTo===Nt.tdkey,containerClass:this.namespace,width:this.consistentMenuWidth?"target":void 0,minWidth:"target",placement:this.placement},{default:()=>l(rn,{name:"fade-in-scale-up-transition",appear:this.isMounted,onAfterLeave:this.handleMenuAfterLeave},{default:()=>{var e,r,a;return this.mergedShow||this.displayDirective==="show"?((e=this.onRender)===null||e===void 0||e.call(this),vo(l(Ho,Object.assign({},this.menuProps,{ref:"menuRef",onResize:this.handleTriggerOrMenuResize,inlineThemeDisabled:this.inlineThemeDisabled,virtualScroll:this.consistentMenuWidth&&this.virtualScroll,class:[`${this.mergedClsPrefix}-select-menu`,this.themeClass,(r=this.menuProps)===null||r===void 0?void 0:r.class],clsPrefix:this.mergedClsPrefix,focusable:!0,labelField:this.labelField,valueField:this.valueField,autoPending:!0,nodeProps:this.nodeProps,theme:this.mergedTheme.peers.InternalSelectMenu,themeOverrides:this.mergedTheme.peerOverrides.InternalSelectMenu,treeMate:this.treeMate,multiple:this.multiple,size:this.menuSize,renderOption:this.renderOption,renderLabel:this.renderLabel,value:this.mergedValue,style:[(a=this.menuProps)===null||a===void 0?void 0:a.style,this.cssVars],onToggle:this.handleToggle,onScroll:this.handleMenuScroll,onFocus:this.handleMenuFocus,onBlur:this.handleMenuBlur,onKeydown:this.handleMenuKeydown,onTabOut:this.handleMenuTabOut,onMousedown:this.handleMenuMousedown,show:this.mergedShow,showCheckmark:this.showCheckmark,resetMenuOnOptionsChange:this.resetMenuOnOptionsChange,scrollbarProps:this.scrollbarProps}),{empty:()=>{var s,d;return[(d=(s=this.$slots).empty)===null||d===void 0?void 0:d.call(s)]},header:()=>{var s,d;return[(d=(s=this.$slots).header)===null||d===void 0?void 0:d.call(s)]},action:()=>{var s,d;return[(d=(s=this.$slots).action)===null||d===void 0?void 0:d.call(s)]}}),this.displayDirective==="show"?[[go,this.mergedShow],[qt,this.handleMenuClickOutside,void 0,{capture:!0}]]:[[qt,this.handleMenuClickOutside,void 0,{capture:!0}]])):null}})})]}))}});export{Ao as C,Ho as N,Bo as V,al as _,sl as a,Qo as c,At as m};
