import{a as c,e as o,g as m,i as a,av as D,aw as I,d as _,h as t,p as $,v as C,q as u,x as z,c as w,cd as B,C as T,D as L,t as H,G as M,an as V,ce as F,V as K}from"./index-Brgn67xP.js";const q=c([o("list",`
 --n-merged-border-color: var(--n-border-color);
 --n-merged-color: var(--n-color);
 --n-merged-color-hover: var(--n-color-hover);
 margin: 0;
 font-size: var(--n-font-size);
 transition:
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 padding: 0;
 list-style-type: none;
 color: var(--n-text-color);
 background-color: var(--n-merged-color);
 `,[m("show-divider",[o("list-item",[c("&:not(:last-child)",[a("divider",`
 background-color: var(--n-merged-border-color);
 `)])])]),m("clickable",[o("list-item",`
 cursor: pointer;
 `)]),m("bordered",`
 border: 1px solid var(--n-merged-border-color);
 border-radius: var(--n-border-radius);
 `),m("hoverable",[o("list-item",`
 border-radius: var(--n-border-radius);
 `,[c("&:hover",`
 background-color: var(--n-merged-color-hover);
 `,[a("divider",`
 background-color: transparent;
 `)])])]),m("bordered, hoverable",[o("list-item",`
 padding: 12px 20px;
 `),a("header, footer",`
 padding: 12px 20px;
 `)]),a("header, footer",`
 padding: 12px 0;
 box-sizing: border-box;
 transition: border-color .3s var(--n-bezier);
 `,[c("&:not(:last-child)",`
 border-bottom: 1px solid var(--n-merged-border-color);
 `)]),o("list-item",`
 position: relative;
 padding: 12px 0; 
 box-sizing: border-box;
 display: flex;
 flex-wrap: nowrap;
 align-items: center;
 transition:
 background-color .3s var(--n-bezier),
 border-color .3s var(--n-bezier);
 `,[a("prefix",`
 margin-right: 20px;
 flex: 0;
 `),a("suffix",`
 margin-left: 20px;
 flex: 0;
 `),a("main",`
 flex: 1;
 `),a("divider",`
 height: 1px;
 position: absolute;
 bottom: 0;
 left: 0;
 right: 0;
 background-color: transparent;
 transition: background-color .3s var(--n-bezier);
 pointer-events: none;
 `)])]),D(o("list",`
 --n-merged-color-hover: var(--n-color-hover-modal);
 --n-merged-color: var(--n-color-modal);
 --n-merged-border-color: var(--n-border-color-modal);
 `)),I(o("list",`
 --n-merged-color-hover: var(--n-color-hover-popover);
 --n-merged-color: var(--n-color-popover);
 --n-merged-border-color: var(--n-border-color-popover);
 `))]),G=Object.assign(Object.assign({},u.props),{size:{type:String,default:"medium"},bordered:Boolean,clickable:Boolean,hoverable:Boolean,showDivider:{type:Boolean,default:!0}}),y=T("n-list"),N=_({name:"List",props:G,slots:Object,setup(r){const{mergedClsPrefixRef:e,inlineThemeDisabled:n,mergedRtlRef:s}=$(r),f=C("List",s,e),b=u("List","-list",q,B,r,e);L(y,{showDividerRef:H(r,"showDivider"),mergedClsPrefixRef:e});const h=w(()=>{const{common:{cubicBezierEaseInOut:d},self:{fontSize:v,textColor:i,color:g,colorModal:x,colorPopover:p,borderColor:R,borderColorModal:E,borderColorPopover:S,borderRadius:j,colorHover:P,colorHoverModal:k,colorHoverPopover:O}}=b.value;return{"--n-font-size":v,"--n-bezier":d,"--n-text-color":i,"--n-color":g,"--n-border-radius":j,"--n-border-color":R,"--n-border-color-modal":E,"--n-border-color-popover":S,"--n-color-modal":x,"--n-color-popover":p,"--n-color-hover":P,"--n-color-hover-modal":k,"--n-color-hover-popover":O}}),l=n?z("list",void 0,h,r):void 0;return{mergedClsPrefix:e,rtlEnabled:f,cssVars:n?void 0:h,themeClass:l==null?void 0:l.themeClass,onRender:l==null?void 0:l.onRender}},render(){var r;const{$slots:e,mergedClsPrefix:n,onRender:s}=this;return s==null||s(),t("ul",{class:[`${n}-list`,this.rtlEnabled&&`${n}-list--rtl`,this.bordered&&`${n}-list--bordered`,this.showDivider&&`${n}-list--show-divider`,this.hoverable&&`${n}-list--hoverable`,this.clickable&&`${n}-list--clickable`,this.themeClass],style:this.cssVars},e.header?t("div",{class:`${n}-list__header`},e.header()):null,(r=e.default)===null||r===void 0?void 0:r.call(e),e.footer?t("div",{class:`${n}-list__footer`},e.footer()):null)}}),Q=_({name:"ListItem",slots:Object,setup(){const r=M(y,null);return r||V("list-item","`n-list-item` must be placed in `n-list`."),{showDivider:r.showDividerRef,mergedClsPrefix:r.mergedClsPrefixRef}},render(){const{$slots:r,mergedClsPrefix:e}=this;return t("li",{class:`${e}-list-item`},r.prefix?t("div",{class:`${e}-list-item__prefix`},r.prefix()):null,r.default?t("div",{class:`${e}-list-item__main`},r):null,r.suffix?t("div",{class:`${e}-list-item__suffix`},r.suffix()):null,this.showDivider&&t("div",{class:`${e}-list-item__divider`}))}}),W=o("thing",`
 display: flex;
 transition: color .3s var(--n-bezier);
 font-size: var(--n-font-size);
 color: var(--n-text-color);
`,[o("thing-avatar",`
 margin-right: 12px;
 margin-top: 2px;
 `),o("thing-avatar-header-wrapper",`
 display: flex;
 flex-wrap: nowrap;
 `,[o("thing-header-wrapper",`
 flex: 1;
 `)]),o("thing-main",`
 flex-grow: 1;
 `,[o("thing-header",`
 display: flex;
 margin-bottom: 4px;
 justify-content: space-between;
 align-items: center;
 `,[a("title",`
 font-size: 16px;
 font-weight: var(--n-title-font-weight);
 transition: color .3s var(--n-bezier);
 color: var(--n-title-text-color);
 `)]),a("description",[c("&:not(:last-child)",`
 margin-bottom: 4px;
 `)]),a("content",[c("&:not(:first-child)",`
 margin-top: 12px;
 `)]),a("footer",[c("&:not(:first-child)",`
 margin-top: 12px;
 `)]),a("action",[c("&:not(:first-child)",`
 margin-top: 12px;
 `)])])]),A=Object.assign(Object.assign({},u.props),{title:String,titleExtra:String,description:String,descriptionClass:String,descriptionStyle:[String,Object],content:String,contentClass:String,contentStyle:[String,Object],contentIndented:Boolean}),U=_({name:"Thing",props:A,slots:Object,setup(r,{slots:e}){const{mergedClsPrefixRef:n,inlineThemeDisabled:s,mergedRtlRef:f}=$(r),b=u("Thing","-thing",W,F,r,n),h=C("Thing",f,n),l=w(()=>{const{self:{titleTextColor:v,textColor:i,titleFontWeight:g,fontSize:x},common:{cubicBezierEaseInOut:p}}=b.value;return{"--n-bezier":p,"--n-font-size":x,"--n-text-color":i,"--n-title-font-weight":g,"--n-title-text-color":v}}),d=s?z("thing",void 0,l,r):void 0;return()=>{var v;const{value:i}=n,g=h?h.value:!1;return(v=d==null?void 0:d.onRender)===null||v===void 0||v.call(d),t("div",{class:[`${i}-thing`,d==null?void 0:d.themeClass,g&&`${i}-thing--rtl`],style:s?void 0:l.value},e.avatar&&r.contentIndented?t("div",{class:`${i}-thing-avatar`},e.avatar()):null,t("div",{class:`${i}-thing-main`},!r.contentIndented&&(e.header||r.title||e["header-extra"]||r.titleExtra||e.avatar)?t("div",{class:`${i}-thing-avatar-header-wrapper`},e.avatar?t("div",{class:`${i}-thing-avatar`},e.avatar()):null,e.header||r.title||e["header-extra"]||r.titleExtra?t("div",{class:`${i}-thing-header-wrapper`},t("div",{class:`${i}-thing-header`},e.header||r.title?t("div",{class:`${i}-thing-header__title`},e.header?e.header():r.title):null,e["header-extra"]||r.titleExtra?t("div",{class:`${i}-thing-header__extra`},e["header-extra"]?e["header-extra"]():r.titleExtra):null),e.description||r.description?t("div",{class:[`${i}-thing-main__description`,r.descriptionClass],style:r.descriptionStyle},e.description?e.description():r.description):null):null):t(K,null,e.header||r.title||e["header-extra"]||r.titleExtra?t("div",{class:`${i}-thing-header`},e.header||r.title?t("div",{class:`${i}-thing-header__title`},e.header?e.header():r.title):null,e["header-extra"]||r.titleExtra?t("div",{class:`${i}-thing-header__extra`},e["header-extra"]?e["header-extra"]():r.titleExtra):null):null,e.description||r.description?t("div",{class:[`${i}-thing-main__description`,r.descriptionClass],style:r.descriptionStyle},e.description?e.description():r.description):null),e.default||r.content?t("div",{class:[`${i}-thing-main__content`,r.contentClass],style:r.contentStyle},e.default?e.default():r.content):null,e.footer?t("div",{class:`${i}-thing-main__footer`},e.footer()):null,e.action?t("div",{class:`${i}-thing-main__action`},e.action()):null))}}});export{N as _,Q as a,U as b};
