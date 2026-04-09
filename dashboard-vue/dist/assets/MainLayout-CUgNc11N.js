import{d as N,h as s,r as O,w as Ae,c as g,n as Se,t as q,a as f,b as io,e as d,f as ao,g as z,i as h,u as so,T as co,N as Oe,j as Be,k as ke,l as uo,m as Me,o as mo,p as ee,q as V,s as Ee,v as vo,x as oe,y as ho,z as fo,A as po,B as go,C as te,D as Z,E as bo,F as xo,G as Y,H as Co,I as yo,J as Ie,S as He,K as Le,L as je,M as U,O as re,P as wo,Q,R as ge,U as ve,V as _o,W as ce,X as zo,Y as Re,Z as So,_ as ko,$ as Io,a0 as G,a1 as W,a2 as B,a3 as Ro,a4 as $o,a5 as F,a6 as M,a7 as ie,a8 as Po,a9 as No,aa as $e,ab as To,ac as Ao,ad as Oo,ae as Bo,af as Mo}from"./index-Brgn67xP.js";import{u as Eo,F as Ho}from"./index-Bd271tTC.js";import{N as de}from"./Icon-vD1iHyHe.js";import{C as Lo,N as jo,a as Fo}from"./Dropdown-BQl3Llcm.js";import{g as Vo,u as he,V as Ko,c as ue}from"./Popover-kalenhAK.js";import{f as me,u as Do,_ as Uo}from"./Space-DIu7bKnh.js";import{S as Yo}from"./SearchOutline-Ghmecp7C.js";const Go=N({name:"ChevronDownFilled",render(){return s("svg",{viewBox:"0 0 16 16",fill:"none",xmlns:"http://www.w3.org/2000/svg"},s("path",{d:"M3.20041 5.73966C3.48226 5.43613 3.95681 5.41856 4.26034 5.70041L8 9.22652L11.7397 5.70041C12.0432 5.41856 12.5177 5.43613 12.7996 5.73966C13.0815 6.0432 13.0639 6.51775 12.7603 6.7996L8.51034 10.7996C8.22258 11.0668 7.77743 11.0668 7.48967 10.7996L3.23966 6.7996C2.93613 6.51775 2.91856 6.0432 3.20041 5.73966Z",fill:"currentColor"}))}}),Pe=N({name:"SlotMachineNumber",props:{clsPrefix:{type:String,required:!0},value:{type:[Number,String],required:!0},oldOriginalNumber:{type:Number,default:void 0},newOriginalNumber:{type:Number,default:void 0}},setup(e){const o=O(null),t=O(e.value),i=O(e.value),l=O("up"),r=O(!1),u=g(()=>r.value?`${e.clsPrefix}-base-slot-machine-current-number--${l.value}-scroll`:null),m=g(()=>r.value?`${e.clsPrefix}-base-slot-machine-old-number--${l.value}-scroll`:null);Ae(q(e,"value"),(x,_)=>{t.value=_,i.value=x,Se(c)});function c(){const x=e.newOriginalNumber,_=e.oldOriginalNumber;_===void 0||x===void 0||(x>_?p("up"):_>x&&p("down"))}function p(x){l.value=x,r.value=!1,Se(()=>{var _;(_=o.value)===null||_===void 0||_.offsetWidth,r.value=!0})}return()=>{const{clsPrefix:x}=e;return s("span",{ref:o,class:`${x}-base-slot-machine-number`},t.value!==null?s("span",{class:[`${x}-base-slot-machine-old-number ${x}-base-slot-machine-old-number--top`,m.value]},t.value):null,s("span",{class:[`${x}-base-slot-machine-current-number`,u.value]},s("span",{ref:"numberWrapper",class:[`${x}-base-slot-machine-current-number__inner`,typeof e.value!="number"&&`${x}-base-slot-machine-current-number__inner--not-number`]},i.value)),t.value!==null?s("span",{class:[`${x}-base-slot-machine-old-number ${x}-base-slot-machine-old-number--bottom`,m.value]},t.value):null)}}}),{cubicBezierEaseOut:J}=io;function qo({duration:e=".2s"}={}){return[f("&.fade-up-width-expand-transition-leave-active",{transition:`
 opacity ${e} ${J},
 max-width ${e} ${J},
 transform ${e} ${J}
 `}),f("&.fade-up-width-expand-transition-enter-active",{transition:`
 opacity ${e} ${J},
 max-width ${e} ${J},
 transform ${e} ${J}
 `}),f("&.fade-up-width-expand-transition-enter-to",{opacity:1,transform:"translateX(0) translateY(0)"}),f("&.fade-up-width-expand-transition-enter-from",{maxWidth:"0 !important",opacity:0,transform:"translateY(60%)"}),f("&.fade-up-width-expand-transition-leave-from",{opacity:1,transform:"translateY(0)"}),f("&.fade-up-width-expand-transition-leave-to",{maxWidth:"0 !important",opacity:0,transform:"translateY(60%)"})]}const Wo=f([f("@keyframes n-base-slot-machine-fade-up-in",`
 from {
 transform: translateY(60%);
 opacity: 0;
 }
 to {
 transform: translateY(0);
 opacity: 1;
 }
 `),f("@keyframes n-base-slot-machine-fade-down-in",`
 from {
 transform: translateY(-60%);
 opacity: 0;
 }
 to {
 transform: translateY(0);
 opacity: 1;
 }
 `),f("@keyframes n-base-slot-machine-fade-up-out",`
 from {
 transform: translateY(0%);
 opacity: 1;
 }
 to {
 transform: translateY(-60%);
 opacity: 0;
 }
 `),f("@keyframes n-base-slot-machine-fade-down-out",`
 from {
 transform: translateY(0%);
 opacity: 1;
 }
 to {
 transform: translateY(60%);
 opacity: 0;
 }
 `),d("base-slot-machine",`
 overflow: hidden;
 white-space: nowrap;
 display: inline-block;
 height: 18px;
 line-height: 18px;
 `,[d("base-slot-machine-number",`
 display: inline-block;
 position: relative;
 height: 18px;
 width: .6em;
 max-width: .6em;
 `,[qo({duration:".2s"}),ao({duration:".2s",delay:"0s"}),d("base-slot-machine-old-number",`
 display: inline-block;
 opacity: 0;
 position: absolute;
 left: 0;
 right: 0;
 `,[z("top",{transform:"translateY(-100%)"}),z("bottom",{transform:"translateY(100%)"}),z("down-scroll",{animation:"n-base-slot-machine-fade-down-out .2s cubic-bezier(0, 0, .2, 1)",animationIterationCount:1}),z("up-scroll",{animation:"n-base-slot-machine-fade-up-out .2s cubic-bezier(0, 0, .2, 1)",animationIterationCount:1})]),d("base-slot-machine-current-number",`
 display: inline-block;
 position: absolute;
 left: 0;
 top: 0;
 bottom: 0;
 right: 0;
 opacity: 1;
 transform: translateY(0);
 width: .6em;
 `,[z("down-scroll",{animation:"n-base-slot-machine-fade-down-in .2s cubic-bezier(0, 0, .2, 1)",animationIterationCount:1}),z("up-scroll",{animation:"n-base-slot-machine-fade-up-in .2s cubic-bezier(0, 0, .2, 1)",animationIterationCount:1}),h("inner",`
 display: inline-block;
 position: absolute;
 right: 0;
 top: 0;
 width: .6em;
 `,[z("not-number",`
 right: unset;
 left: 0;
 `)])])])])]),Xo=N({name:"BaseSlotMachine",props:{clsPrefix:{type:String,required:!0},value:{type:[Number,String],default:0},max:{type:Number,default:void 0},appeared:{type:Boolean,required:!0}},setup(e){so("-base-slot-machine",Wo,q(e,"clsPrefix"));const o=O(),t=O(),i=g(()=>{if(typeof e.value=="string")return[];if(e.value<1)return[0];const l=[];let r=e.value;for(e.max!==void 0&&(r=Math.min(e.max,r));r>=1;)l.push(r%10),r/=10,r=Math.floor(r);return l.reverse(),l});return Ae(q(e,"value"),(l,r)=>{typeof l=="string"?(t.value=void 0,o.value=void 0):typeof r=="string"?(t.value=l,o.value=void 0):(t.value=l,o.value=r)}),()=>{const{value:l,clsPrefix:r}=e;return typeof l=="number"?s("span",{class:`${r}-base-slot-machine`},s(co,{name:"fade-up-width-expand-transition",tag:"span"},{default:()=>i.value.map((u,m)=>s(Pe,{clsPrefix:r,key:i.value.length-m-1,oldOriginalNumber:o.value,newOriginalNumber:t.value,value:u}))}),s(Oe,{key:"+",width:!0},{default:()=>e.max!==void 0&&e.max<l?s(Pe,{clsPrefix:r,value:"+"}):null})):s("span",{class:`${r}-base-slot-machine`},l)}}});function Zo(e){const{errorColor:o,infoColor:t,successColor:i,warningColor:l,fontFamily:r}=e;return{color:o,colorInfo:t,colorSuccess:i,colorError:o,colorWarning:l,fontSize:"12px",fontFamily:r}}const Jo={common:Be,self:Zo},Qo=f([f("@keyframes badge-wave-spread",{from:{boxShadow:"0 0 0.5px 0px var(--n-ripple-color)",opacity:.6},to:{boxShadow:"0 0 0.5px 4.5px var(--n-ripple-color)",opacity:0}}),d("badge",`
 display: inline-flex;
 position: relative;
 vertical-align: middle;
 font-family: var(--n-font-family);
 `,[z("as-is",[d("badge-sup",{position:"static",transform:"translateX(0)"},[ke({transformOrigin:"left bottom",originalTransform:"translateX(0)"})])]),z("dot",[d("badge-sup",`
 height: 8px;
 width: 8px;
 padding: 0;
 min-width: 8px;
 left: 100%;
 bottom: calc(100% - 4px);
 `,[f("::before","border-radius: 4px;")])]),d("badge-sup",`
 background: var(--n-color);
 transition:
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier);
 color: #FFF;
 position: absolute;
 height: 18px;
 line-height: 18px;
 border-radius: 9px;
 padding: 0 6px;
 text-align: center;
 font-size: var(--n-font-size);
 transform: translateX(-50%);
 left: 100%;
 bottom: calc(100% - 9px);
 font-variant-numeric: tabular-nums;
 z-index: 2;
 display: flex;
 align-items: center;
 `,[ke({transformOrigin:"left bottom",originalTransform:"translateX(-50%)"}),d("base-wave",{zIndex:1,animationDuration:"2s",animationIterationCount:"infinite",animationDelay:"1s",animationTimingFunction:"var(--n-ripple-bezier)",animationName:"badge-wave-spread"}),f("&::before",`
 opacity: 0;
 transform: scale(1);
 border-radius: 9px;
 content: "";
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 `)])])]),et=Object.assign(Object.assign({},V.props),{value:[String,Number],max:Number,dot:Boolean,type:{type:String,default:"default"},show:{type:Boolean,default:!0},showZero:Boolean,processing:Boolean,color:String,offset:Array}),ot=N({name:"Badge",props:et,setup(e,{slots:o}){const{mergedClsPrefixRef:t,inlineThemeDisabled:i,mergedRtlRef:l}=ee(e),r=V("Badge","-badge",Qo,Jo,e,t),u=O(!1),m=()=>{u.value=!0},c=()=>{u.value=!1},p=g(()=>e.show&&(e.dot||e.value!==void 0&&!(!e.showZero&&Number(e.value)<=0)||!ho(o.value)));Ee(()=>{p.value&&(u.value=!0)});const x=vo("Badge",l,t),_=g(()=>{const{type:S,color:C}=e,{common:{cubicBezierEaseInOut:y,cubicBezierEaseOut:P},self:{[fo("color",S)]:L,fontFamily:j,fontSize:E}}=r.value;return{"--n-font-size":E,"--n-font-family":j,"--n-color":C||L,"--n-ripple-color":C||L,"--n-bezier":y,"--n-ripple-bezier":P}}),v=i?oe("badge",g(()=>{let S="";const{type:C,color:y}=e;return C&&(S+=C[0]),y&&(S+=po(y)),S}),_,e):void 0,$=g(()=>{const{offset:S}=e;if(!S)return;const[C,y]=S,P=typeof C=="number"?`${C}px`:C,L=typeof y=="number"?`${y}px`:y;return{transform:`translate(calc(${x!=null&&x.value?"50%":"-50%"} + ${P}), ${L})`}});return{rtlEnabled:x,mergedClsPrefix:t,appeared:u,showBadge:p,handleAfterEnter:m,handleAfterLeave:c,cssVars:i?void 0:_,themeClass:v==null?void 0:v.themeClass,onRender:v==null?void 0:v.onRender,offsetStyle:$}},render(){var e;const{mergedClsPrefix:o,onRender:t,themeClass:i,$slots:l}=this;t==null||t();const r=(e=l.default)===null||e===void 0?void 0:e.call(l);return s("div",{class:[`${o}-badge`,this.rtlEnabled&&`${o}-badge--rtl`,i,{[`${o}-badge--dot`]:this.dot,[`${o}-badge--as-is`]:!r}],style:this.cssVars},r,s(uo,{name:"fade-in-scale-up-transition",onAfterEnter:this.handleAfterEnter,onAfterLeave:this.handleAfterLeave},{default:()=>this.showBadge?s("sup",{class:`${o}-badge-sup`,title:Vo(this.value),style:this.offsetStyle},Me(l.value,()=>[this.dot?null:s(Xo,{clsPrefix:o,appeared:this.appeared,max:this.max,value:this.value})]),this.processing?s(mo,{clsPrefix:o}):null):null}))}}),tt=d("breadcrumb",`
 white-space: nowrap;
 cursor: default;
 line-height: var(--n-item-line-height);
`,[f("ul",`
 list-style: none;
 padding: 0;
 margin: 0;
 `),f("a",`
 color: inherit;
 text-decoration: inherit;
 `),d("breadcrumb-item",`
 font-size: var(--n-font-size);
 transition: color .3s var(--n-bezier);
 display: inline-flex;
 align-items: center;
 `,[d("icon",`
 font-size: 18px;
 vertical-align: -.2em;
 transition: color .3s var(--n-bezier);
 color: var(--n-item-text-color);
 `),f("&:not(:last-child)",[z("clickable",[h("link",`
 cursor: pointer;
 `,[f("&:hover",`
 background-color: var(--n-item-color-hover);
 `),f("&:active",`
 background-color: var(--n-item-color-pressed); 
 `)])])]),h("link",`
 padding: 4px;
 border-radius: var(--n-item-border-radius);
 transition:
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier);
 color: var(--n-item-text-color);
 position: relative;
 `,[f("&:hover",`
 color: var(--n-item-text-color-hover);
 `,[d("icon",`
 color: var(--n-item-text-color-hover);
 `)]),f("&:active",`
 color: var(--n-item-text-color-pressed);
 `,[d("icon",`
 color: var(--n-item-text-color-pressed);
 `)])]),h("separator",`
 margin: 0 8px;
 color: var(--n-separator-color);
 transition: color .3s var(--n-bezier);
 user-select: none;
 -webkit-user-select: none;
 `),f("&:last-child",[h("link",`
 font-weight: var(--n-font-weight-active);
 cursor: unset;
 color: var(--n-item-text-color-active);
 `,[d("icon",`
 color: var(--n-item-text-color-active);
 `)]),h("separator",`
 display: none;
 `)])])]),Fe=te("n-breadcrumb"),rt=Object.assign(Object.assign({},V.props),{separator:{type:String,default:"/"}}),nt=N({name:"Breadcrumb",props:rt,setup(e){const{mergedClsPrefixRef:o,inlineThemeDisabled:t}=ee(e),i=V("Breadcrumb","-breadcrumb",tt,go,e,o);Z(Fe,{separatorRef:q(e,"separator"),mergedClsPrefixRef:o});const l=g(()=>{const{common:{cubicBezierEaseInOut:u},self:{separatorColor:m,itemTextColor:c,itemTextColorHover:p,itemTextColorPressed:x,itemTextColorActive:_,fontSize:v,fontWeightActive:$,itemBorderRadius:S,itemColorHover:C,itemColorPressed:y,itemLineHeight:P}}=i.value;return{"--n-font-size":v,"--n-bezier":u,"--n-item-text-color":c,"--n-item-text-color-hover":p,"--n-item-text-color-pressed":x,"--n-item-text-color-active":_,"--n-separator-color":m,"--n-item-color-hover":C,"--n-item-color-pressed":y,"--n-item-border-radius":S,"--n-font-weight-active":$,"--n-item-line-height":P}}),r=t?oe("breadcrumb",void 0,l,e):void 0;return{mergedClsPrefix:o,cssVars:t?void 0:l,themeClass:r==null?void 0:r.themeClass,onRender:r==null?void 0:r.onRender}},render(){var e;return(e=this.onRender)===null||e===void 0||e.call(this),s("nav",{class:[`${this.mergedClsPrefix}-breadcrumb`,this.themeClass],style:this.cssVars,"aria-label":"Breadcrumb"},s("ul",null,this.$slots))}});function lt(e=xo?window:null){const o=()=>{const{hash:l,host:r,hostname:u,href:m,origin:c,pathname:p,port:x,protocol:_,search:v}=(e==null?void 0:e.location)||{};return{hash:l,host:r,hostname:u,href:m,origin:c,pathname:p,port:x,protocol:_,search:v}},t=O(o()),i=()=>{t.value=o()};return Ee(()=>{e&&(e.addEventListener("popstate",i),e.addEventListener("hashchange",i))}),bo(()=>{e&&(e.removeEventListener("popstate",i),e.removeEventListener("hashchange",i))}),t}const it={separator:String,href:String,clickable:{type:Boolean,default:!0},showSeparator:{type:Boolean,default:!0},onClick:Function},at=N({name:"BreadcrumbItem",props:it,slots:Object,setup(e,{slots:o}){const t=Y(Fe,null);if(!t)return()=>null;const{separatorRef:i,mergedClsPrefixRef:l}=t,r=lt(),u=g(()=>e.href?"a":"span"),m=g(()=>r.value.href===e.href?"location":null);return()=>{const{value:c}=l;return s("li",{class:[`${c}-breadcrumb-item`,e.clickable&&`${c}-breadcrumb-item--clickable`]},s(u.value,{class:`${c}-breadcrumb-item__link`,"aria-current":m.value,href:e.href,onClick:e.onClick},o),e.showSeparator&&s("span",{class:`${c}-breadcrumb-item__separator`,"aria-hidden":"true"},Me(o.separator,()=>{var p;return[(p=e.separator)!==null&&p!==void 0?p:i.value]})))}}});function st(e){const{baseColor:o,textColor2:t,bodyColor:i,cardColor:l,dividerColor:r,actionColor:u,scrollbarColor:m,scrollbarColorHover:c,invertedColor:p}=e;return{textColor:t,textColorInverted:"#FFF",color:i,colorEmbedded:u,headerColor:l,headerColorInverted:p,footerColor:u,footerColorInverted:p,headerBorderColor:r,headerBorderColorInverted:p,footerBorderColor:r,footerBorderColorInverted:p,siderBorderColor:r,siderBorderColorInverted:p,siderColor:l,siderColorInverted:p,siderToggleButtonBorder:`1px solid ${r}`,siderToggleButtonColor:o,siderToggleButtonIconColor:t,siderToggleButtonIconColorInverted:t,siderToggleBarColor:Ie(i,m),siderToggleBarColorHover:Ie(i,c),__invertScrollbar:"true"}}const be=Co({name:"Layout",common:Be,peers:{Scrollbar:yo},self:st}),Ve=te("n-layout-sider"),xe={type:String,default:"static"},ct=d("layout",`
 color: var(--n-text-color);
 background-color: var(--n-color);
 box-sizing: border-box;
 position: relative;
 z-index: auto;
 flex: auto;
 overflow: hidden;
 transition:
 box-shadow .3s var(--n-bezier),
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier);
`,[d("layout-scroll-container",`
 overflow-x: hidden;
 box-sizing: border-box;
 height: 100%;
 `),z("absolute-positioned",`
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 `)]),dt={embedded:Boolean,position:xe,nativeScrollbar:{type:Boolean,default:!0},scrollbarProps:Object,onScroll:Function,contentClass:String,contentStyle:{type:[String,Object],default:""},hasSider:Boolean,siderPlacement:{type:String,default:"left"}},Ke=te("n-layout");function De(e){return N({name:e?"LayoutContent":"Layout",props:Object.assign(Object.assign({},V.props),dt),setup(o){const t=O(null),i=O(null),{mergedClsPrefixRef:l,inlineThemeDisabled:r}=ee(o),u=V("Layout","-layout",ct,be,o,l);function m(C,y){if(o.nativeScrollbar){const{value:P}=t;P&&(y===void 0?P.scrollTo(C):P.scrollTo(C,y))}else{const{value:P}=i;P&&P.scrollTo(C,y)}}Z(Ke,o);let c=0,p=0;const x=C=>{var y;const P=C.target;c=P.scrollLeft,p=P.scrollTop,(y=o.onScroll)===null||y===void 0||y.call(o,C)};Le(()=>{if(o.nativeScrollbar){const C=t.value;C&&(C.scrollTop=p,C.scrollLeft=c)}});const _={display:"flex",flexWrap:"nowrap",width:"100%",flexDirection:"row"},v={scrollTo:m},$=g(()=>{const{common:{cubicBezierEaseInOut:C},self:y}=u.value;return{"--n-bezier":C,"--n-color":o.embedded?y.colorEmbedded:y.color,"--n-text-color":y.textColor}}),S=r?oe("layout",g(()=>o.embedded?"e":""),$,o):void 0;return Object.assign({mergedClsPrefix:l,scrollableElRef:t,scrollbarInstRef:i,hasSiderStyle:_,mergedTheme:u,handleNativeElScroll:x,cssVars:r?void 0:$,themeClass:S==null?void 0:S.themeClass,onRender:S==null?void 0:S.onRender},v)},render(){var o;const{mergedClsPrefix:t,hasSider:i}=this;(o=this.onRender)===null||o===void 0||o.call(this);const l=i?this.hasSiderStyle:void 0,r=[this.themeClass,e&&`${t}-layout-content`,`${t}-layout`,`${t}-layout--${this.position}-positioned`];return s("div",{class:r,style:this.cssVars},this.nativeScrollbar?s("div",{ref:"scrollableElRef",class:[`${t}-layout-scroll-container`,this.contentClass],style:[this.contentStyle,l],onScroll:this.handleNativeElScroll},this.$slots):s(He,Object.assign({},this.scrollbarProps,{onScroll:this.onScroll,ref:"scrollbarInstRef",theme:this.mergedTheme.peers.Scrollbar,themeOverrides:this.mergedTheme.peerOverrides.Scrollbar,contentClass:this.contentClass,contentStyle:[this.contentStyle,l]}),this.$slots))}})}const ut=De(!1),mt=De(!0),vt=d("layout-header",`
 transition:
 color .3s var(--n-bezier),
 background-color .3s var(--n-bezier),
 box-shadow .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 box-sizing: border-box;
 width: 100%;
 background-color: var(--n-color);
 color: var(--n-text-color);
`,[z("absolute-positioned",`
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 `),z("bordered",`
 border-bottom: solid 1px var(--n-border-color);
 `)]),ht={position:xe,inverted:Boolean,bordered:{type:Boolean,default:!1}},ft=N({name:"LayoutHeader",props:Object.assign(Object.assign({},V.props),ht),setup(e){const{mergedClsPrefixRef:o,inlineThemeDisabled:t}=ee(e),i=V("Layout","-layout-header",vt,be,e,o),l=g(()=>{const{common:{cubicBezierEaseInOut:u},self:m}=i.value,c={"--n-bezier":u};return e.inverted?(c["--n-color"]=m.headerColorInverted,c["--n-text-color"]=m.textColorInverted,c["--n-border-color"]=m.headerBorderColorInverted):(c["--n-color"]=m.headerColor,c["--n-text-color"]=m.textColor,c["--n-border-color"]=m.headerBorderColor),c}),r=t?oe("layout-header",g(()=>e.inverted?"a":"b"),l,e):void 0;return{mergedClsPrefix:o,cssVars:t?void 0:l,themeClass:r==null?void 0:r.themeClass,onRender:r==null?void 0:r.onRender}},render(){var e;const{mergedClsPrefix:o}=this;return(e=this.onRender)===null||e===void 0||e.call(this),s("div",{class:[`${o}-layout-header`,this.themeClass,this.position&&`${o}-layout-header--${this.position}-positioned`,this.bordered&&`${o}-layout-header--bordered`],style:this.cssVars},this.$slots)}}),pt=d("layout-sider",`
 flex-shrink: 0;
 box-sizing: border-box;
 position: relative;
 z-index: 1;
 color: var(--n-text-color);
 transition:
 color .3s var(--n-bezier),
 border-color .3s var(--n-bezier),
 min-width .3s var(--n-bezier),
 max-width .3s var(--n-bezier),
 transform .3s var(--n-bezier),
 background-color .3s var(--n-bezier);
 background-color: var(--n-color);
 display: flex;
 justify-content: flex-end;
`,[z("bordered",[h("border",`
 content: "";
 position: absolute;
 top: 0;
 bottom: 0;
 width: 1px;
 background-color: var(--n-border-color);
 transition: background-color .3s var(--n-bezier);
 `)]),h("left-placement",[z("bordered",[h("border",`
 right: 0;
 `)])]),z("right-placement",`
 justify-content: flex-start;
 `,[z("bordered",[h("border",`
 left: 0;
 `)]),z("collapsed",[d("layout-toggle-button",[d("base-icon",`
 transform: rotate(180deg);
 `)]),d("layout-toggle-bar",[f("&:hover",[h("top",{transform:"rotate(-12deg) scale(1.15) translateY(-2px)"}),h("bottom",{transform:"rotate(12deg) scale(1.15) translateY(2px)"})])])]),d("layout-toggle-button",`
 left: 0;
 transform: translateX(-50%) translateY(-50%);
 `,[d("base-icon",`
 transform: rotate(0);
 `)]),d("layout-toggle-bar",`
 left: -28px;
 transform: rotate(180deg);
 `,[f("&:hover",[h("top",{transform:"rotate(12deg) scale(1.15) translateY(-2px)"}),h("bottom",{transform:"rotate(-12deg) scale(1.15) translateY(2px)"})])])]),z("collapsed",[d("layout-toggle-bar",[f("&:hover",[h("top",{transform:"rotate(-12deg) scale(1.15) translateY(-2px)"}),h("bottom",{transform:"rotate(12deg) scale(1.15) translateY(2px)"})])]),d("layout-toggle-button",[d("base-icon",`
 transform: rotate(0);
 `)])]),d("layout-toggle-button",`
 transition:
 color .3s var(--n-bezier),
 right .3s var(--n-bezier),
 left .3s var(--n-bezier),
 border-color .3s var(--n-bezier),
 background-color .3s var(--n-bezier);
 cursor: pointer;
 width: 24px;
 height: 24px;
 position: absolute;
 top: 50%;
 right: 0;
 border-radius: 50%;
 display: flex;
 align-items: center;
 justify-content: center;
 font-size: 18px;
 color: var(--n-toggle-button-icon-color);
 border: var(--n-toggle-button-border);
 background-color: var(--n-toggle-button-color);
 box-shadow: 0 2px 4px 0px rgba(0, 0, 0, .06);
 transform: translateX(50%) translateY(-50%);
 z-index: 1;
 `,[d("base-icon",`
 transition: transform .3s var(--n-bezier);
 transform: rotate(180deg);
 `)]),d("layout-toggle-bar",`
 cursor: pointer;
 height: 72px;
 width: 32px;
 position: absolute;
 top: calc(50% - 36px);
 right: -28px;
 `,[h("top, bottom",`
 position: absolute;
 width: 4px;
 border-radius: 2px;
 height: 38px;
 left: 14px;
 transition: 
 background-color .3s var(--n-bezier),
 transform .3s var(--n-bezier);
 `),h("bottom",`
 position: absolute;
 top: 34px;
 `),f("&:hover",[h("top",{transform:"rotate(12deg) scale(1.15) translateY(-2px)"}),h("bottom",{transform:"rotate(-12deg) scale(1.15) translateY(2px)"})]),h("top, bottom",{backgroundColor:"var(--n-toggle-bar-color)"}),f("&:hover",[h("top, bottom",{backgroundColor:"var(--n-toggle-bar-color-hover)"})])]),h("border",`
 position: absolute;
 top: 0;
 right: 0;
 bottom: 0;
 width: 1px;
 transition: background-color .3s var(--n-bezier);
 `),d("layout-sider-scroll-container",`
 flex-grow: 1;
 flex-shrink: 0;
 box-sizing: border-box;
 height: 100%;
 opacity: 0;
 transition: opacity .3s var(--n-bezier);
 max-width: 100%;
 `),z("show-content",[d("layout-sider-scroll-container",{opacity:1})]),z("absolute-positioned",`
 position: absolute;
 left: 0;
 top: 0;
 bottom: 0;
 `)]),gt=N({props:{clsPrefix:{type:String,required:!0},onClick:Function},render(){const{clsPrefix:e}=this;return s("div",{onClick:this.onClick,class:`${e}-layout-toggle-bar`},s("div",{class:`${e}-layout-toggle-bar__top`}),s("div",{class:`${e}-layout-toggle-bar__bottom`}))}}),bt=N({name:"LayoutToggleButton",props:{clsPrefix:{type:String,required:!0},onClick:Function},render(){const{clsPrefix:e}=this;return s("div",{class:`${e}-layout-toggle-button`,onClick:this.onClick},s(je,{clsPrefix:e},{default:()=>s(Lo,null)}))}}),xt={position:xe,bordered:Boolean,collapsedWidth:{type:Number,default:48},width:{type:[Number,String],default:272},contentClass:String,contentStyle:{type:[String,Object],default:""},collapseMode:{type:String,default:"transform"},collapsed:{type:Boolean,default:void 0},defaultCollapsed:Boolean,showCollapsedContent:{type:Boolean,default:!0},showTrigger:{type:[Boolean,String],default:!1},nativeScrollbar:{type:Boolean,default:!0},inverted:Boolean,scrollbarProps:Object,triggerClass:String,triggerStyle:[String,Object],collapsedTriggerClass:String,collapsedTriggerStyle:[String,Object],"onUpdate:collapsed":[Function,Array],onUpdateCollapsed:[Function,Array],onAfterEnter:Function,onAfterLeave:Function,onExpand:[Function,Array],onCollapse:[Function,Array],onScroll:Function},Ct=N({name:"LayoutSider",props:Object.assign(Object.assign({},V.props),xt),setup(e){const o=Y(Ke),t=O(null),i=O(null),l=O(e.defaultCollapsed),r=he(q(e,"collapsed"),l),u=g(()=>me(r.value?e.collapsedWidth:e.width)),m=g(()=>e.collapseMode!=="transform"?{}:{minWidth:me(e.width)}),c=g(()=>o?o.siderPlacement:"left");function p(T,k){if(e.nativeScrollbar){const{value:I}=t;I&&(k===void 0?I.scrollTo(T):I.scrollTo(T,k))}else{const{value:I}=i;I&&I.scrollTo(T,k)}}function x(){const{"onUpdate:collapsed":T,onUpdateCollapsed:k,onExpand:I,onCollapse:K}=e,{value:D}=r;k&&U(k,!D),T&&U(T,!D),l.value=!D,D?I&&U(I):K&&U(K)}let _=0,v=0;const $=T=>{var k;const I=T.target;_=I.scrollLeft,v=I.scrollTop,(k=e.onScroll)===null||k===void 0||k.call(e,T)};Le(()=>{if(e.nativeScrollbar){const T=t.value;T&&(T.scrollTop=v,T.scrollLeft=_)}}),Z(Ve,{collapsedRef:r,collapseModeRef:q(e,"collapseMode")});const{mergedClsPrefixRef:S,inlineThemeDisabled:C}=ee(e),y=V("Layout","-layout-sider",pt,be,e,S);function P(T){var k,I;T.propertyName==="max-width"&&(r.value?(k=e.onAfterLeave)===null||k===void 0||k.call(e):(I=e.onAfterEnter)===null||I===void 0||I.call(e))}const L={scrollTo:p},j=g(()=>{const{common:{cubicBezierEaseInOut:T},self:k}=y.value,{siderToggleButtonColor:I,siderToggleButtonBorder:K,siderToggleBarColor:D,siderToggleBarColorHover:se}=k,H={"--n-bezier":T,"--n-toggle-button-color":I,"--n-toggle-button-border":K,"--n-toggle-bar-color":D,"--n-toggle-bar-color-hover":se};return e.inverted?(H["--n-color"]=k.siderColorInverted,H["--n-text-color"]=k.textColorInverted,H["--n-border-color"]=k.siderBorderColorInverted,H["--n-toggle-button-icon-color"]=k.siderToggleButtonIconColorInverted,H.__invertScrollbar=k.__invertScrollbar):(H["--n-color"]=k.siderColor,H["--n-text-color"]=k.textColor,H["--n-border-color"]=k.siderBorderColor,H["--n-toggle-button-icon-color"]=k.siderToggleButtonIconColor),H}),E=C?oe("layout-sider",g(()=>e.inverted?"a":"b"),j,e):void 0;return Object.assign({scrollableElRef:t,scrollbarInstRef:i,mergedClsPrefix:S,mergedTheme:y,styleMaxWidth:u,mergedCollapsed:r,scrollContainerStyle:m,siderPlacement:c,handleNativeElScroll:$,handleTransitionend:P,handleTriggerClick:x,inlineThemeDisabled:C,cssVars:j,themeClass:E==null?void 0:E.themeClass,onRender:E==null?void 0:E.onRender},L)},render(){var e;const{mergedClsPrefix:o,mergedCollapsed:t,showTrigger:i}=this;return(e=this.onRender)===null||e===void 0||e.call(this),s("aside",{class:[`${o}-layout-sider`,this.themeClass,`${o}-layout-sider--${this.position}-positioned`,`${o}-layout-sider--${this.siderPlacement}-placement`,this.bordered&&`${o}-layout-sider--bordered`,t&&`${o}-layout-sider--collapsed`,(!t||this.showCollapsedContent)&&`${o}-layout-sider--show-content`],onTransitionend:this.handleTransitionend,style:[this.inlineThemeDisabled?void 0:this.cssVars,{maxWidth:this.styleMaxWidth,width:me(this.width)}]},this.nativeScrollbar?s("div",{class:[`${o}-layout-sider-scroll-container`,this.contentClass],onScroll:this.handleNativeElScroll,style:[this.scrollContainerStyle,{overflow:"auto"},this.contentStyle],ref:"scrollableElRef"},this.$slots):s(He,Object.assign({},this.scrollbarProps,{onScroll:this.onScroll,ref:"scrollbarInstRef",style:this.scrollContainerStyle,contentStyle:this.contentStyle,contentClass:this.contentClass,theme:this.mergedTheme.peers.Scrollbar,themeOverrides:this.mergedTheme.peerOverrides.Scrollbar,builtinThemeOverrides:this.inverted&&this.cssVars.__invertScrollbar==="true"?{colorHover:"rgba(255, 255, 255, .4)",color:"rgba(255, 255, 255, .3)"}:void 0}),this.$slots),i?i==="bar"?s(gt,{clsPrefix:o,class:t?this.collapsedTriggerClass:this.triggerClass,style:t?this.collapsedTriggerStyle:this.triggerStyle,onClick:this.handleTriggerClick}):s(bt,{clsPrefix:o,class:t?this.collapsedTriggerClass:this.triggerClass,style:t?this.collapsedTriggerStyle:this.triggerStyle,onClick:this.handleTriggerClick}):null,this.bordered?s("div",{class:`${o}-layout-sider__border`}):null)}}),ne=te("n-menu"),Ue=te("n-submenu"),Ce=te("n-menu-item-group"),Ne=[f("&::before","background-color: var(--n-item-color-hover);"),h("arrow",`
 color: var(--n-arrow-color-hover);
 `),h("icon",`
 color: var(--n-item-icon-color-hover);
 `),d("menu-item-content-header",`
 color: var(--n-item-text-color-hover);
 `,[f("a",`
 color: var(--n-item-text-color-hover);
 `),h("extra",`
 color: var(--n-item-text-color-hover);
 `)])],Te=[h("icon",`
 color: var(--n-item-icon-color-hover-horizontal);
 `),d("menu-item-content-header",`
 color: var(--n-item-text-color-hover-horizontal);
 `,[f("a",`
 color: var(--n-item-text-color-hover-horizontal);
 `),h("extra",`
 color: var(--n-item-text-color-hover-horizontal);
 `)])],yt=f([d("menu",`
 background-color: var(--n-color);
 color: var(--n-item-text-color);
 overflow: hidden;
 transition: background-color .3s var(--n-bezier);
 box-sizing: border-box;
 font-size: var(--n-font-size);
 padding-bottom: 6px;
 `,[z("horizontal",`
 max-width: 100%;
 width: 100%;
 display: flex;
 overflow: hidden;
 padding-bottom: 0;
 `,[d("submenu","margin: 0;"),d("menu-item","margin: 0;"),d("menu-item-content",`
 padding: 0 20px;
 border-bottom: 2px solid #0000;
 `,[f("&::before","display: none;"),z("selected","border-bottom: 2px solid var(--n-border-color-horizontal)")]),d("menu-item-content",[z("selected",[h("icon","color: var(--n-item-icon-color-active-horizontal);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-active-horizontal);
 `,[f("a","color: var(--n-item-text-color-active-horizontal);"),h("extra","color: var(--n-item-text-color-active-horizontal);")])]),z("child-active",`
 border-bottom: 2px solid var(--n-border-color-horizontal);
 `,[d("menu-item-content-header",`
 color: var(--n-item-text-color-child-active-horizontal);
 `,[f("a",`
 color: var(--n-item-text-color-child-active-horizontal);
 `),h("extra",`
 color: var(--n-item-text-color-child-active-horizontal);
 `)]),h("icon",`
 color: var(--n-item-icon-color-child-active-horizontal);
 `)]),re("disabled",[re("selected, child-active",[f("&:focus-within",Te)]),z("selected",[X(null,[h("icon","color: var(--n-item-icon-color-active-hover-horizontal);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-active-hover-horizontal);
 `,[f("a","color: var(--n-item-text-color-active-hover-horizontal);"),h("extra","color: var(--n-item-text-color-active-hover-horizontal);")])])]),z("child-active",[X(null,[h("icon","color: var(--n-item-icon-color-child-active-hover-horizontal);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-child-active-hover-horizontal);
 `,[f("a","color: var(--n-item-text-color-child-active-hover-horizontal);"),h("extra","color: var(--n-item-text-color-child-active-hover-horizontal);")])])]),X("border-bottom: 2px solid var(--n-border-color-horizontal);",Te)]),d("menu-item-content-header",[f("a","color: var(--n-item-text-color-horizontal);")])])]),re("responsive",[d("menu-item-content-header",`
 overflow: hidden;
 text-overflow: ellipsis;
 `)]),z("collapsed",[d("menu-item-content",[z("selected",[f("&::before",`
 background-color: var(--n-item-color-active-collapsed) !important;
 `)]),d("menu-item-content-header","opacity: 0;"),h("arrow","opacity: 0;"),h("icon","color: var(--n-item-icon-color-collapsed);")])]),d("menu-item",`
 height: var(--n-item-height);
 margin-top: 6px;
 position: relative;
 `),d("menu-item-content",`
 box-sizing: border-box;
 line-height: 1.75;
 height: 100%;
 display: grid;
 grid-template-areas: "icon content arrow";
 grid-template-columns: auto 1fr auto;
 align-items: center;
 cursor: pointer;
 position: relative;
 padding-right: 18px;
 transition:
 background-color .3s var(--n-bezier),
 padding-left .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 `,[f("> *","z-index: 1;"),f("&::before",`
 z-index: auto;
 content: "";
 background-color: #0000;
 position: absolute;
 left: 8px;
 right: 8px;
 top: 0;
 bottom: 0;
 pointer-events: none;
 border-radius: var(--n-border-radius);
 transition: background-color .3s var(--n-bezier);
 `),z("disabled",`
 opacity: .45;
 cursor: not-allowed;
 `),z("collapsed",[h("arrow","transform: rotate(0);")]),z("selected",[f("&::before","background-color: var(--n-item-color-active);"),h("arrow","color: var(--n-arrow-color-active);"),h("icon","color: var(--n-item-icon-color-active);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-active);
 `,[f("a","color: var(--n-item-text-color-active);"),h("extra","color: var(--n-item-text-color-active);")])]),z("child-active",[d("menu-item-content-header",`
 color: var(--n-item-text-color-child-active);
 `,[f("a",`
 color: var(--n-item-text-color-child-active);
 `),h("extra",`
 color: var(--n-item-text-color-child-active);
 `)]),h("arrow",`
 color: var(--n-arrow-color-child-active);
 `),h("icon",`
 color: var(--n-item-icon-color-child-active);
 `)]),re("disabled",[re("selected, child-active",[f("&:focus-within",Ne)]),z("selected",[X(null,[h("arrow","color: var(--n-arrow-color-active-hover);"),h("icon","color: var(--n-item-icon-color-active-hover);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-active-hover);
 `,[f("a","color: var(--n-item-text-color-active-hover);"),h("extra","color: var(--n-item-text-color-active-hover);")])])]),z("child-active",[X(null,[h("arrow","color: var(--n-arrow-color-child-active-hover);"),h("icon","color: var(--n-item-icon-color-child-active-hover);"),d("menu-item-content-header",`
 color: var(--n-item-text-color-child-active-hover);
 `,[f("a","color: var(--n-item-text-color-child-active-hover);"),h("extra","color: var(--n-item-text-color-child-active-hover);")])])]),z("selected",[X(null,[f("&::before","background-color: var(--n-item-color-active-hover);")])]),X(null,Ne)]),h("icon",`
 grid-area: icon;
 color: var(--n-item-icon-color);
 transition:
 color .3s var(--n-bezier),
 font-size .3s var(--n-bezier),
 margin-right .3s var(--n-bezier);
 box-sizing: content-box;
 display: inline-flex;
 align-items: center;
 justify-content: center;
 `),h("arrow",`
 grid-area: arrow;
 font-size: 16px;
 color: var(--n-arrow-color);
 transform: rotate(180deg);
 opacity: 1;
 transition:
 color .3s var(--n-bezier),
 transform 0.2s var(--n-bezier),
 opacity 0.2s var(--n-bezier);
 `),d("menu-item-content-header",`
 grid-area: content;
 transition:
 color .3s var(--n-bezier),
 opacity .3s var(--n-bezier);
 opacity: 1;
 white-space: nowrap;
 color: var(--n-item-text-color);
 `,[f("a",`
 outline: none;
 text-decoration: none;
 transition: color .3s var(--n-bezier);
 color: var(--n-item-text-color);
 `,[f("&::before",`
 content: "";
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 `)]),h("extra",`
 font-size: .93em;
 color: var(--n-group-text-color);
 transition: color .3s var(--n-bezier);
 `)])]),d("submenu",`
 cursor: pointer;
 position: relative;
 margin-top: 6px;
 `,[d("menu-item-content",`
 height: var(--n-item-height);
 `),d("submenu-children",`
 overflow: hidden;
 padding: 0;
 `,[wo({duration:".2s"})])]),d("menu-item-group",[d("menu-item-group-title",`
 margin-top: 6px;
 color: var(--n-group-text-color);
 cursor: default;
 font-size: .93em;
 height: 36px;
 display: flex;
 align-items: center;
 transition:
 padding-left .3s var(--n-bezier),
 color .3s var(--n-bezier);
 `)])]),d("menu-tooltip",[f("a",`
 color: inherit;
 text-decoration: none;
 `)]),d("menu-divider",`
 transition: background-color .3s var(--n-bezier);
 background-color: var(--n-divider-color);
 height: 1px;
 margin: 6px 18px;
 `)]);function X(e,o){return[z("hover",e,o),f("&:hover",e,o)]}const Ye=N({name:"MenuOptionContent",props:{collapsed:Boolean,disabled:Boolean,title:[String,Function],icon:Function,extra:[String,Function],showArrow:Boolean,childActive:Boolean,hover:Boolean,paddingLeft:Number,selected:Boolean,maxIconSize:{type:Number,required:!0},activeIconSize:{type:Number,required:!0},iconMarginRight:{type:Number,required:!0},clsPrefix:{type:String,required:!0},onClick:Function,tmNode:{type:Object,required:!0},isEllipsisPlaceholder:Boolean},setup(e){const{props:o}=Y(ne);return{menuProps:o,style:g(()=>{const{paddingLeft:t}=e;return{paddingLeft:t&&`${t}px`}}),iconStyle:g(()=>{const{maxIconSize:t,activeIconSize:i,iconMarginRight:l}=e;return{width:`${t}px`,height:`${t}px`,fontSize:`${i}px`,marginRight:`${l}px`}})}},render(){const{clsPrefix:e,tmNode:o,menuProps:{renderIcon:t,renderLabel:i,renderExtra:l,expandIcon:r}}=this,u=t?t(o.rawNode):Q(this.icon);return s("div",{onClick:m=>{var c;(c=this.onClick)===null||c===void 0||c.call(this,m)},role:"none",class:[`${e}-menu-item-content`,{[`${e}-menu-item-content--selected`]:this.selected,[`${e}-menu-item-content--collapsed`]:this.collapsed,[`${e}-menu-item-content--child-active`]:this.childActive,[`${e}-menu-item-content--disabled`]:this.disabled,[`${e}-menu-item-content--hover`]:this.hover}],style:this.style},u&&s("div",{class:`${e}-menu-item-content__icon`,style:this.iconStyle,role:"none"},[u]),s("div",{class:`${e}-menu-item-content-header`,role:"none"},this.isEllipsisPlaceholder?this.title:i?i(o.rawNode):Q(this.title),this.extra||l?s("span",{class:`${e}-menu-item-content-header__extra`}," ",l?l(o.rawNode):Q(this.extra)):null),this.showArrow?s(je,{ariaHidden:!0,class:`${e}-menu-item-content__arrow`,clsPrefix:e},{default:()=>r?r(o.rawNode):s(Go,null)}):null)}}),ae=8;function ye(e){const o=Y(ne),{props:t,mergedCollapsedRef:i}=o,l=Y(Ue,null),r=Y(Ce,null),u=g(()=>t.mode==="horizontal"),m=g(()=>u.value?t.dropdownPlacement:"tmNodes"in e?"right-start":"right"),c=g(()=>{var v;return Math.max((v=t.collapsedIconSize)!==null&&v!==void 0?v:t.iconSize,t.iconSize)}),p=g(()=>{var v;return!u.value&&e.root&&i.value&&(v=t.collapsedIconSize)!==null&&v!==void 0?v:t.iconSize}),x=g(()=>{if(u.value)return;const{collapsedWidth:v,indent:$,rootIndent:S}=t,{root:C,isGroup:y}=e,P=S===void 0?$:S;return C?i.value?v/2-c.value/2:P:r&&typeof r.paddingLeftRef.value=="number"?$/2+r.paddingLeftRef.value:l&&typeof l.paddingLeftRef.value=="number"?(y?$/2:$)+l.paddingLeftRef.value:0}),_=g(()=>{const{collapsedWidth:v,indent:$,rootIndent:S}=t,{value:C}=c,{root:y}=e;return u.value||!y||!i.value?ae:(S===void 0?$:S)+C+ae-(v+C)/2});return{dropdownPlacement:m,activeIconSize:p,maxIconSize:c,paddingLeft:x,iconMarginRight:_,NMenu:o,NSubmenu:l,NMenuOptionGroup:r}}const we={internalKey:{type:[String,Number],required:!0},root:Boolean,isGroup:Boolean,level:{type:Number,required:!0},title:[String,Function],extra:[String,Function]},wt=N({name:"MenuDivider",setup(){const e=Y(ne),{mergedClsPrefixRef:o,isHorizontalRef:t}=e;return()=>t.value?null:s("div",{class:`${o.value}-menu-divider`})}}),Ge=Object.assign(Object.assign({},we),{tmNode:{type:Object,required:!0},disabled:Boolean,icon:Function,onClick:Function}),_t=ge(Ge),zt=N({name:"MenuOption",props:Ge,setup(e){const o=ye(e),{NSubmenu:t,NMenu:i,NMenuOptionGroup:l}=o,{props:r,mergedClsPrefixRef:u,mergedCollapsedRef:m}=i,c=t?t.mergedDisabledRef:l?l.mergedDisabledRef:{value:!1},p=g(()=>c.value||e.disabled);function x(v){const{onClick:$}=e;$&&$(v)}function _(v){p.value||(i.doSelect(e.internalKey,e.tmNode.rawNode),x(v))}return{mergedClsPrefix:u,dropdownPlacement:o.dropdownPlacement,paddingLeft:o.paddingLeft,iconMarginRight:o.iconMarginRight,maxIconSize:o.maxIconSize,activeIconSize:o.activeIconSize,mergedTheme:i.mergedThemeRef,menuProps:r,dropdownEnabled:ve(()=>e.root&&m.value&&r.mode!=="horizontal"&&!p.value),selected:ve(()=>i.mergedValueRef.value===e.internalKey),mergedDisabled:p,handleClick:_}},render(){const{mergedClsPrefix:e,mergedTheme:o,tmNode:t,menuProps:{renderLabel:i,nodeProps:l}}=this,r=l==null?void 0:l(t.rawNode);return s("div",Object.assign({},r,{role:"menuitem",class:[`${e}-menu-item`,r==null?void 0:r.class]}),s(jo,{theme:o.peers.Tooltip,themeOverrides:o.peerOverrides.Tooltip,trigger:"hover",placement:this.dropdownPlacement,disabled:!this.dropdownEnabled||this.title===void 0,internalExtraClass:["menu-tooltip"]},{default:()=>i?i(t.rawNode):Q(this.title),trigger:()=>s(Ye,{tmNode:t,clsPrefix:e,paddingLeft:this.paddingLeft,iconMarginRight:this.iconMarginRight,maxIconSize:this.maxIconSize,activeIconSize:this.activeIconSize,selected:this.selected,title:this.title,extra:this.extra,disabled:this.mergedDisabled,icon:this.icon,onClick:this.handleClick})}))}}),qe=Object.assign(Object.assign({},we),{tmNode:{type:Object,required:!0},tmNodes:{type:Array,required:!0}}),St=ge(qe),kt=N({name:"MenuOptionGroup",props:qe,setup(e){const o=ye(e),{NSubmenu:t}=o,i=g(()=>t!=null&&t.mergedDisabledRef.value?!0:e.tmNode.disabled);Z(Ce,{paddingLeftRef:o.paddingLeft,mergedDisabledRef:i});const{mergedClsPrefixRef:l,props:r}=Y(ne);return function(){const{value:u}=l,m=o.paddingLeft.value,{nodeProps:c}=r,p=c==null?void 0:c(e.tmNode.rawNode);return s("div",{class:`${u}-menu-item-group`,role:"group"},s("div",Object.assign({},p,{class:[`${u}-menu-item-group-title`,p==null?void 0:p.class],style:[(p==null?void 0:p.style)||"",m!==void 0?`padding-left: ${m}px;`:""]}),Q(e.title),e.extra?s(_o,null," ",Q(e.extra)):null),s("div",null,e.tmNodes.map(x=>_e(x,r))))}}});function fe(e){return e.type==="divider"||e.type==="render"}function It(e){return e.type==="divider"}function _e(e,o){const{rawNode:t}=e,{show:i}=t;if(i===!1)return null;if(fe(t))return It(t)?s(wt,Object.assign({key:e.key},t.props)):null;const{labelField:l}=o,{key:r,level:u,isGroup:m}=e,c=Object.assign(Object.assign({},t),{title:t.title||t[l],extra:t.titleExtra||t.extra,key:r,internalKey:r,level:u,root:u===0,isGroup:m});return e.children?e.isGroup?s(kt,ce(c,St,{tmNode:e,tmNodes:e.children,key:r})):s(pe,ce(c,Rt,{key:r,rawNodes:t[o.childrenField],tmNodes:e.children,tmNode:e})):s(zt,ce(c,_t,{key:r,tmNode:e}))}const We=Object.assign(Object.assign({},we),{rawNodes:{type:Array,default:()=>[]},tmNodes:{type:Array,default:()=>[]},tmNode:{type:Object,required:!0},disabled:Boolean,icon:Function,onClick:Function,domId:String,virtualChildActive:{type:Boolean,default:void 0},isEllipsisPlaceholder:Boolean}),Rt=ge(We),pe=N({name:"Submenu",props:We,setup(e){const o=ye(e),{NMenu:t,NSubmenu:i}=o,{props:l,mergedCollapsedRef:r,mergedThemeRef:u}=t,m=g(()=>{const{disabled:v}=e;return i!=null&&i.mergedDisabledRef.value||l.disabled?!0:v}),c=O(!1);Z(Ue,{paddingLeftRef:o.paddingLeft,mergedDisabledRef:m}),Z(Ce,null);function p(){const{onClick:v}=e;v&&v()}function x(){m.value||(r.value||t.toggleExpand(e.internalKey),p())}function _(v){c.value=v}return{menuProps:l,mergedTheme:u,doSelect:t.doSelect,inverted:t.invertedRef,isHorizontal:t.isHorizontalRef,mergedClsPrefix:t.mergedClsPrefixRef,maxIconSize:o.maxIconSize,activeIconSize:o.activeIconSize,iconMarginRight:o.iconMarginRight,dropdownPlacement:o.dropdownPlacement,dropdownShow:c,paddingLeft:o.paddingLeft,mergedDisabled:m,mergedValue:t.mergedValueRef,childActive:ve(()=>{var v;return(v=e.virtualChildActive)!==null&&v!==void 0?v:t.activePathRef.value.includes(e.internalKey)}),collapsed:g(()=>l.mode==="horizontal"?!1:r.value?!0:!t.mergedExpandedKeysRef.value.includes(e.internalKey)),dropdownEnabled:g(()=>!m.value&&(l.mode==="horizontal"||r.value)),handlePopoverShowChange:_,handleClick:x}},render(){var e;const{mergedClsPrefix:o,menuProps:{renderIcon:t,renderLabel:i}}=this,l=()=>{const{isHorizontal:u,paddingLeft:m,collapsed:c,mergedDisabled:p,maxIconSize:x,activeIconSize:_,title:v,childActive:$,icon:S,handleClick:C,menuProps:{nodeProps:y},dropdownShow:P,iconMarginRight:L,tmNode:j,mergedClsPrefix:E,isEllipsisPlaceholder:T,extra:k}=this,I=y==null?void 0:y(j.rawNode);return s("div",Object.assign({},I,{class:[`${E}-menu-item`,I==null?void 0:I.class],role:"menuitem"}),s(Ye,{tmNode:j,paddingLeft:m,collapsed:c,disabled:p,iconMarginRight:L,maxIconSize:x,activeIconSize:_,title:v,extra:k,showArrow:!u,childActive:$,clsPrefix:E,icon:S,hover:P,onClick:C,isEllipsisPlaceholder:T}))},r=()=>s(Oe,null,{default:()=>{const{tmNodes:u,collapsed:m}=this;return m?null:s("div",{class:`${o}-submenu-children`,role:"menu"},u.map(c=>_e(c,this.menuProps)))}});return this.root?s(Fo,Object.assign({size:"large",trigger:"hover"},(e=this.menuProps)===null||e===void 0?void 0:e.dropdownProps,{themeOverrides:this.mergedTheme.peerOverrides.Dropdown,theme:this.mergedTheme.peers.Dropdown,builtinThemeOverrides:{fontSizeLarge:"14px",optionIconSizeLarge:"18px"},value:this.mergedValue,disabled:!this.dropdownEnabled,placement:this.dropdownPlacement,keyField:this.menuProps.keyField,labelField:this.menuProps.labelField,childrenField:this.menuProps.childrenField,onUpdateShow:this.handlePopoverShowChange,options:this.rawNodes,onSelect:this.doSelect,inverted:this.inverted,renderIcon:t,renderLabel:i}),{default:()=>s("div",{class:`${o}-submenu`,role:"menu","aria-expanded":!this.collapsed,id:this.domId},l(),this.isHorizontal?null:r())}):s("div",{class:`${o}-submenu`,role:"menu","aria-expanded":!this.collapsed,id:this.domId},l(),r())}}),$t=Object.assign(Object.assign({},V.props),{options:{type:Array,default:()=>[]},collapsed:{type:Boolean,default:void 0},collapsedWidth:{type:Number,default:48},iconSize:{type:Number,default:20},collapsedIconSize:{type:Number,default:24},rootIndent:Number,indent:{type:Number,default:32},labelField:{type:String,default:"label"},keyField:{type:String,default:"key"},childrenField:{type:String,default:"children"},disabledField:{type:String,default:"disabled"},defaultExpandAll:Boolean,defaultExpandedKeys:Array,expandedKeys:Array,value:[String,Number],defaultValue:{type:[String,Number],default:null},mode:{type:String,default:"vertical"},watchProps:{type:Array,default:void 0},disabled:Boolean,show:{type:Boolean,default:!0},inverted:Boolean,"onUpdate:expandedKeys":[Function,Array],onUpdateExpandedKeys:[Function,Array],onUpdateValue:[Function,Array],"onUpdate:value":[Function,Array],expandIcon:Function,renderIcon:Function,renderLabel:Function,renderExtra:Function,dropdownProps:Object,accordion:Boolean,nodeProps:Function,dropdownPlacement:{type:String,default:"bottom"},responsive:Boolean,items:Array,onOpenNamesChange:[Function,Array],onSelect:[Function,Array],onExpandedNamesChange:[Function,Array],expandedNames:Array,defaultExpandedNames:Array}),Pt=N({name:"Menu",inheritAttrs:!1,props:$t,setup(e){const{mergedClsPrefixRef:o,inlineThemeDisabled:t}=ee(e),i=V("Menu","-menu",yt,Io,e,o),l=Y(Ve,null),r=g(()=>{var b;const{collapsed:R}=e;if(R!==void 0)return R;if(l){const{collapseModeRef:n,collapsedRef:w}=l;if(n.value==="width")return(b=w.value)!==null&&b!==void 0?b:!1}return!1}),u=g(()=>{const{keyField:b,childrenField:R,disabledField:n}=e;return ue(e.items||e.options,{getIgnored(w){return fe(w)},getChildren(w){return w[R]},getDisabled(w){return w[n]},getKey(w){var A;return(A=w[b])!==null&&A!==void 0?A:w.name}})}),m=g(()=>new Set(u.value.treeNodes.map(b=>b.key))),{watchProps:c}=e,p=O(null);c!=null&&c.includes("defaultValue")?Re(()=>{p.value=e.defaultValue}):p.value=e.defaultValue;const x=q(e,"value"),_=he(x,p),v=O([]),$=()=>{v.value=e.defaultExpandAll?u.value.getNonLeafKeys():e.defaultExpandedNames||e.defaultExpandedKeys||u.value.getPath(_.value,{includeSelf:!1}).keyPath};c!=null&&c.includes("defaultExpandedKeys")?Re($):$();const S=Do(e,["expandedNames","expandedKeys"]),C=he(S,v),y=g(()=>u.value.treeNodes),P=g(()=>u.value.getPath(_.value).keyPath);Z(ne,{props:e,mergedCollapsedRef:r,mergedThemeRef:i,mergedValueRef:_,mergedExpandedKeysRef:C,activePathRef:P,mergedClsPrefixRef:o,isHorizontalRef:g(()=>e.mode==="horizontal"),invertedRef:q(e,"inverted"),doSelect:L,toggleExpand:E});function L(b,R){const{"onUpdate:value":n,onUpdateValue:w,onSelect:A}=e;w&&U(w,b,R),n&&U(n,b,R),A&&U(A,b,R),p.value=b}function j(b){const{"onUpdate:expandedKeys":R,onUpdateExpandedKeys:n,onExpandedNamesChange:w,onOpenNamesChange:A}=e;R&&U(R,b),n&&U(n,b),w&&U(w,b),A&&U(A,b),v.value=b}function E(b){const R=Array.from(C.value),n=R.findIndex(w=>w===b);if(~n)R.splice(n,1);else{if(e.accordion&&m.value.has(b)){const w=R.findIndex(A=>m.value.has(A));w>-1&&R.splice(w,1)}R.push(b)}j(R)}const T=b=>{const R=u.value.getPath(b??_.value,{includeSelf:!1}).keyPath;if(!R.length)return;const n=Array.from(C.value),w=new Set([...n,...R]);e.accordion&&m.value.forEach(A=>{w.has(A)&&!R.includes(A)&&w.delete(A)}),j(Array.from(w))},k=g(()=>{const{inverted:b}=e,{common:{cubicBezierEaseInOut:R},self:n}=i.value,{borderRadius:w,borderColorHorizontal:A,fontSize:ro,itemHeight:no,dividerColor:lo}=n,a={"--n-divider-color":lo,"--n-bezier":R,"--n-font-size":ro,"--n-border-color-horizontal":A,"--n-border-radius":w,"--n-item-height":no};return b?(a["--n-group-text-color"]=n.groupTextColorInverted,a["--n-color"]=n.colorInverted,a["--n-item-text-color"]=n.itemTextColorInverted,a["--n-item-text-color-hover"]=n.itemTextColorHoverInverted,a["--n-item-text-color-active"]=n.itemTextColorActiveInverted,a["--n-item-text-color-child-active"]=n.itemTextColorChildActiveInverted,a["--n-item-text-color-child-active-hover"]=n.itemTextColorChildActiveInverted,a["--n-item-text-color-active-hover"]=n.itemTextColorActiveHoverInverted,a["--n-item-icon-color"]=n.itemIconColorInverted,a["--n-item-icon-color-hover"]=n.itemIconColorHoverInverted,a["--n-item-icon-color-active"]=n.itemIconColorActiveInverted,a["--n-item-icon-color-active-hover"]=n.itemIconColorActiveHoverInverted,a["--n-item-icon-color-child-active"]=n.itemIconColorChildActiveInverted,a["--n-item-icon-color-child-active-hover"]=n.itemIconColorChildActiveHoverInverted,a["--n-item-icon-color-collapsed"]=n.itemIconColorCollapsedInverted,a["--n-item-text-color-horizontal"]=n.itemTextColorHorizontalInverted,a["--n-item-text-color-hover-horizontal"]=n.itemTextColorHoverHorizontalInverted,a["--n-item-text-color-active-horizontal"]=n.itemTextColorActiveHorizontalInverted,a["--n-item-text-color-child-active-horizontal"]=n.itemTextColorChildActiveHorizontalInverted,a["--n-item-text-color-child-active-hover-horizontal"]=n.itemTextColorChildActiveHoverHorizontalInverted,a["--n-item-text-color-active-hover-horizontal"]=n.itemTextColorActiveHoverHorizontalInverted,a["--n-item-icon-color-horizontal"]=n.itemIconColorHorizontalInverted,a["--n-item-icon-color-hover-horizontal"]=n.itemIconColorHoverHorizontalInverted,a["--n-item-icon-color-active-horizontal"]=n.itemIconColorActiveHorizontalInverted,a["--n-item-icon-color-active-hover-horizontal"]=n.itemIconColorActiveHoverHorizontalInverted,a["--n-item-icon-color-child-active-horizontal"]=n.itemIconColorChildActiveHorizontalInverted,a["--n-item-icon-color-child-active-hover-horizontal"]=n.itemIconColorChildActiveHoverHorizontalInverted,a["--n-arrow-color"]=n.arrowColorInverted,a["--n-arrow-color-hover"]=n.arrowColorHoverInverted,a["--n-arrow-color-active"]=n.arrowColorActiveInverted,a["--n-arrow-color-active-hover"]=n.arrowColorActiveHoverInverted,a["--n-arrow-color-child-active"]=n.arrowColorChildActiveInverted,a["--n-arrow-color-child-active-hover"]=n.arrowColorChildActiveHoverInverted,a["--n-item-color-hover"]=n.itemColorHoverInverted,a["--n-item-color-active"]=n.itemColorActiveInverted,a["--n-item-color-active-hover"]=n.itemColorActiveHoverInverted,a["--n-item-color-active-collapsed"]=n.itemColorActiveCollapsedInverted):(a["--n-group-text-color"]=n.groupTextColor,a["--n-color"]=n.color,a["--n-item-text-color"]=n.itemTextColor,a["--n-item-text-color-hover"]=n.itemTextColorHover,a["--n-item-text-color-active"]=n.itemTextColorActive,a["--n-item-text-color-child-active"]=n.itemTextColorChildActive,a["--n-item-text-color-child-active-hover"]=n.itemTextColorChildActiveHover,a["--n-item-text-color-active-hover"]=n.itemTextColorActiveHover,a["--n-item-icon-color"]=n.itemIconColor,a["--n-item-icon-color-hover"]=n.itemIconColorHover,a["--n-item-icon-color-active"]=n.itemIconColorActive,a["--n-item-icon-color-active-hover"]=n.itemIconColorActiveHover,a["--n-item-icon-color-child-active"]=n.itemIconColorChildActive,a["--n-item-icon-color-child-active-hover"]=n.itemIconColorChildActiveHover,a["--n-item-icon-color-collapsed"]=n.itemIconColorCollapsed,a["--n-item-text-color-horizontal"]=n.itemTextColorHorizontal,a["--n-item-text-color-hover-horizontal"]=n.itemTextColorHoverHorizontal,a["--n-item-text-color-active-horizontal"]=n.itemTextColorActiveHorizontal,a["--n-item-text-color-child-active-horizontal"]=n.itemTextColorChildActiveHorizontal,a["--n-item-text-color-child-active-hover-horizontal"]=n.itemTextColorChildActiveHoverHorizontal,a["--n-item-text-color-active-hover-horizontal"]=n.itemTextColorActiveHoverHorizontal,a["--n-item-icon-color-horizontal"]=n.itemIconColorHorizontal,a["--n-item-icon-color-hover-horizontal"]=n.itemIconColorHoverHorizontal,a["--n-item-icon-color-active-horizontal"]=n.itemIconColorActiveHorizontal,a["--n-item-icon-color-active-hover-horizontal"]=n.itemIconColorActiveHoverHorizontal,a["--n-item-icon-color-child-active-horizontal"]=n.itemIconColorChildActiveHorizontal,a["--n-item-icon-color-child-active-hover-horizontal"]=n.itemIconColorChildActiveHoverHorizontal,a["--n-arrow-color"]=n.arrowColor,a["--n-arrow-color-hover"]=n.arrowColorHover,a["--n-arrow-color-active"]=n.arrowColorActive,a["--n-arrow-color-active-hover"]=n.arrowColorActiveHover,a["--n-arrow-color-child-active"]=n.arrowColorChildActive,a["--n-arrow-color-child-active-hover"]=n.arrowColorChildActiveHover,a["--n-item-color-hover"]=n.itemColorHover,a["--n-item-color-active"]=n.itemColorActive,a["--n-item-color-active-hover"]=n.itemColorActiveHover,a["--n-item-color-active-collapsed"]=n.itemColorActiveCollapsed),a}),I=t?oe("menu",g(()=>e.inverted?"a":"b"),k,e):void 0,K=So(),D=O(null),se=O(null);let H=!0;const ze=()=>{var b;H?H=!1:(b=D.value)===null||b===void 0||b.sync({showAllItemsBeforeCalculate:!0})};function Xe(){return document.getElementById(K)}const le=O(-1);function Ze(b){le.value=e.options.length-b}function Je(b){b||(le.value=-1)}const Qe=g(()=>{const b=le.value;return{children:b===-1?[]:e.options.slice(b)}}),eo=g(()=>{const{childrenField:b,disabledField:R,keyField:n}=e;return ue([Qe.value],{getIgnored(w){return fe(w)},getChildren(w){return w[b]},getDisabled(w){return w[R]},getKey(w){var A;return(A=w[n])!==null&&A!==void 0?A:w.name}})}),oo=g(()=>ue([{}]).treeNodes[0]);function to(){var b;if(le.value===-1)return s(pe,{root:!0,level:0,key:"__ellpisisGroupPlaceholder__",internalKey:"__ellpisisGroupPlaceholder__",title:"···",tmNode:oo.value,domId:K,isEllipsisPlaceholder:!0});const R=eo.value.treeNodes[0],n=P.value,w=!!(!((b=R.children)===null||b===void 0)&&b.some(A=>n.includes(A.key)));return s(pe,{level:0,root:!0,key:"__ellpisisGroup__",internalKey:"__ellpisisGroup__",title:"···",virtualChildActive:w,tmNode:R,domId:K,rawNodes:R.rawNode.children||[],tmNodes:R.children||[],isEllipsisPlaceholder:!0})}return{mergedClsPrefix:o,controlledExpandedKeys:S,uncontrolledExpanededKeys:v,mergedExpandedKeys:C,uncontrolledValue:p,mergedValue:_,activePath:P,tmNodes:y,mergedTheme:i,mergedCollapsed:r,cssVars:t?void 0:k,themeClass:I==null?void 0:I.themeClass,overflowRef:D,counterRef:se,updateCounter:()=>{},onResize:ze,onUpdateOverflow:Je,onUpdateCount:Ze,renderCounter:to,getCounter:Xe,onRender:I==null?void 0:I.onRender,showOption:T,deriveResponsiveState:ze}},render(){const{mergedClsPrefix:e,mode:o,themeClass:t,onRender:i}=this;i==null||i();const l=()=>this.tmNodes.map(c=>_e(c,this.$props)),u=o==="horizontal"&&this.responsive,m=()=>s("div",ko(this.$attrs,{role:o==="horizontal"?"menubar":"menu",class:[`${e}-menu`,t,`${e}-menu--${o}`,u&&`${e}-menu--responsive`,this.mergedCollapsed&&`${e}-menu--collapsed`],style:this.cssVars}),u?s(Ko,{ref:"overflowRef",onUpdateOverflow:this.onUpdateOverflow,getCounter:this.getCounter,onUpdateCount:this.onUpdateCount,updateCounter:this.updateCounter,style:{width:"100%",display:"flex",overflow:"hidden"}},{default:l,counter:this.renderCounter}):l());return u?s(zo,{onResize:this.onResize},{default:m}):m()}}),Nt={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},Tt=B("path",{d:"M256 160c16-63.16 76.43-95.41 208-96a15.94 15.94 0 0 1 16 16v288a16 16 0 0 1-16 16c-128 0-177.45 25.81-208 64c-30.37-38-80-64-208-64c-9.88 0-16-8.05-16-17.93V80a15.94 15.94 0 0 1 16-16c131.57.59 192 32.84 208 96z",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),At=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M256 160v288"},null,-1),Ot=[Tt,At],Bt=N({name:"BookOutline",render:function(o,t){return G(),W("svg",Nt,Ot)}}),Mt={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},Et=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M160 368L32 256l128-112"},null,-1),Ht=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M352 368l128-112l-128-112"},null,-1),Lt=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M304 96l-96 320"},null,-1),jt=[Et,Ht,Lt],Ft=N({name:"CodeSlashOutline",render:function(o,t){return G(),W("svg",Mt,jt)}}),Vt={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},Kt=B("path",{d:"M448 341.37V170.61A32 32 0 0 0 432.11 143l-152-88.46a47.94 47.94 0 0 0-48.24 0L79.89 143A32 32 0 0 0 64 170.61v170.76A32 32 0 0 0 79.89 369l152 88.46a48 48 0 0 0 48.24 0l152-88.46A32 32 0 0 0 448 341.37z",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),Dt=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M69 153.99l187 110l187-110"},null,-1),Ut=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M256 463.99v-200"},null,-1),Yt=[Kt,Dt,Ut],Gt=N({name:"CubeOutline",render:function(o,t){return G(),W("svg",Vt,Yt)}}),qt={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},Wt=B("path",{d:"M80 212v236a16 16 0 0 0 16 16h96V328a24 24 0 0 1 24-24h80a24 24 0 0 1 24 24v136h96a16 16 0 0 0 16-16V212",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),Xt=B("path",{d:"M480 256L266.89 52c-5-5.28-16.69-5.34-21.78 0L32 256",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),Zt=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M400 179V64h-48v69"},null,-1),Jt=[Wt,Xt,Zt],Qt=N({name:"HomeOutline",render:function(o,t){return G(),W("svg",qt,Jt)}}),er={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},or=B("path",{d:"M160 136c0-30.62 4.51-61.61 16-88C99.57 81.27 48 159.32 48 248c0 119.29 96.71 216 216 216c88.68 0 166.73-51.57 200-128c-26.39 11.49-57.38 16-88 16c-119.29 0-216-96.71-216-216z",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),tr=[or],rr=N({name:"MoonOutline",render:function(o,t){return G(),W("svg",er,tr)}}),nr={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},lr=B("path",{fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32",d:"M48 320h64l64-256l64 384l64-224l32 96h64"},null,-1),ir=B("circle",{cx:"432",cy:"320",r:"32",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),ar=[lr,ir],sr=N({name:"PulseOutline",render:function(o,t){return G(),W("svg",nr,ar)}}),cr={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},dr=B("path",{d:"M262.29 192.31a64 64 0 1 0 57.4 57.4a64.13 64.13 0 0 0-57.4-57.4zM416.39 256a154.34 154.34 0 0 1-1.53 20.79l45.21 35.46a10.81 10.81 0 0 1 2.45 13.75l-42.77 74a10.81 10.81 0 0 1-13.14 4.59l-44.9-18.08a16.11 16.11 0 0 0-15.17 1.75A164.48 164.48 0 0 1 325 400.8a15.94 15.94 0 0 0-8.82 12.14l-6.73 47.89a11.08 11.08 0 0 1-10.68 9.17h-85.54a11.11 11.11 0 0 1-10.69-8.87l-6.72-47.82a16.07 16.07 0 0 0-9-12.22a155.3 155.3 0 0 1-21.46-12.57a16 16 0 0 0-15.11-1.71l-44.89 18.07a10.81 10.81 0 0 1-13.14-4.58l-42.77-74a10.8 10.8 0 0 1 2.45-13.75l38.21-30a16.05 16.05 0 0 0 6-14.08c-.36-4.17-.58-8.33-.58-12.5s.21-8.27.58-12.35a16 16 0 0 0-6.07-13.94l-38.19-30A10.81 10.81 0 0 1 49.48 186l42.77-74a10.81 10.81 0 0 1 13.14-4.59l44.9 18.08a16.11 16.11 0 0 0 15.17-1.75A164.48 164.48 0 0 1 187 111.2a15.94 15.94 0 0 0 8.82-12.14l6.73-47.89A11.08 11.08 0 0 1 213.23 42h85.54a11.11 11.11 0 0 1 10.69 8.87l6.72 47.82a16.07 16.07 0 0 0 9 12.22a155.3 155.3 0 0 1 21.46 12.57a16 16 0 0 0 15.11 1.71l44.89-18.07a10.81 10.81 0 0 1 13.14 4.58l42.77 74a10.8 10.8 0 0 1-2.45 13.75l-38.21 30a16.05 16.05 0 0 0-6.05 14.08c.33 4.14.55 8.3.55 12.47z",fill:"none",stroke:"currentColor","stroke-linecap":"round","stroke-linejoin":"round","stroke-width":"32"},null,-1),ur=[dr],mr=N({name:"SettingsOutline",render:function(o,t){return G(),W("svg",cr,ur)}}),vr={xmlns:"http://www.w3.org/2000/svg","xmlns:xlink":"http://www.w3.org/1999/xlink",viewBox:"0 0 512 512"},hr=Ro('<path d="M326.1 231.9l-47.5 75.5a31 31 0 0 1-7 7a30.11 30.11 0 0 1-35-49l75.5-47.5a10.23 10.23 0 0 1 11.7 0a10.06 10.06 0 0 1 2.3 14z" fill="currentColor"></path><path d="M256 64C132.3 64 32 164.2 32 287.9a223.18 223.18 0 0 0 56.3 148.5c1.1 1.2 2.1 2.4 3.2 3.5a25.19 25.19 0 0 0 37.1-.1a173.13 173.13 0 0 1 254.8 0a25.19 25.19 0 0 0 37.1.1l3.2-3.5A223.18 223.18 0 0 0 480 287.9C480 164.2 379.7 64 256 64z" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="32"></path><path fill="none" stroke="currentColor" stroke-linecap="round" stroke-miterlimit="10" stroke-width="32" d="M256 128v32"></path><path fill="none" stroke="currentColor" stroke-linecap="round" stroke-miterlimit="10" stroke-width="32" d="M416 288h-32"></path><path fill="none" stroke="currentColor" stroke-linecap="round" stroke-miterlimit="10" stroke-width="32" d="M128 288H96"></path><path fill="none" stroke="currentColor" stroke-linecap="round" stroke-miterlimit="10" stroke-width="32" d="M165.49 197.49l-22.63-22.63"></path><path fill="none" stroke="currentColor" stroke-linecap="round" stroke-miterlimit="10" stroke-width="32" d="M346.51 197.49l22.63-22.63"></path>',7),fr=[hr],pr=N({name:"SpeedometerOutline",render:function(o,t){return G(),W("svg",vr,fr)}}),gr={class:"logo"},br={class:"logo-text"},xr=N({__name:"MainLayout",setup(e){const o=Bo(),t=Mo(),i=Eo(),l=O(!1),r=g(()=>i.isConnected?"connected":"disconnected"),u=g(()=>t.name),m=g(()=>t.meta.title||"Dashboard"),c=v=>()=>s(de,null,{default:()=>s(v)}),p=[{label:"Dashboard",key:"Dashboard",icon:c(Qt)},{label:"向量搜索",key:"Search",icon:c(Yo)},{label:"源管理",key:"Sources",icon:c(Ho)},{label:"模型管理",key:"Models",icon:c(Gt)},{label:"系统监控",key:"System",icon:c(sr)},{label:"设置",key:"Settings",icon:c(mr)},{label:"性能测试",key:"Performance",icon:c(pr)},{label:"API 文档",key:"ApiHelp",icon:c(Ft)}],x=v=>{o.push({name:v})},_=()=>{};return(v,$)=>{const S=Pt,C=Ct,y=at,P=nt,L=ot,j=Ao,E=Uo,T=ft,k=Oo("router-view"),I=mt,K=ut;return G(),$o(K,{"has-sider":"",style:{height:"100vh"}},{default:F(()=>[M(C,{bordered:"","collapse-mode":"width","collapsed-width":64,width:240,collapsed:l.value,"show-trigger":"",onCollapse:$[0]||($[0]=D=>l.value=!0),onExpand:$[1]||($[1]=D=>l.value=!1)},{default:F(()=>[B("div",gr,[M(ie(de),{size:"28",color:"#63e2b7"},{default:F(()=>[M(ie(Bt))]),_:1}),Po(B("span",br,"Obsidian RAG",512),[[No,!l.value]])]),M(S,{collapsed:l.value,"collapsed-width":64,"collapsed-icon-size":22,options:p,value:u.value,"onUpdate:value":x},null,8,["collapsed","value"])]),_:1},8,["collapsed"]),M(K,null,{default:F(()=>[M(T,{bordered:"",style:{height:"60px",padding:"0 20px",display:"flex","align-items":"center","justify-content":"space-between"}},{default:F(()=>[M(P,null,{default:F(()=>[M(y,null,{default:F(()=>[...$[2]||($[2]=[$e("Obsidian RAG",-1)])]),_:1}),M(y,null,{default:F(()=>[$e(To(m.value),1)]),_:1})]),_:1}),M(E,{align:"center"},{default:F(()=>[M(L,{value:r.value,type:r.value==="connected"?"success":"error"},null,8,["value","type"]),M(j,{text:"",onClick:_},{icon:F(()=>[M(ie(de),null,{default:F(()=>[M(ie(rr))]),_:1})]),_:1})]),_:1})]),_:1}),M(I,{style:{padding:"20px",overflow:"auto"}},{default:F(()=>[M(k)]),_:1})]),_:1})]),_:1})}}}),Cr=(e,o)=>{const t=e.__vccOpts||e;for(const[i,l]of o)t[i]=l;return t},Rr=Cr(xr,[["__scopeId","data-v-447b4e92"]]);export{Rr as default};
