import { createElementVNode as _createElementVNode, openBlock as _openBlock, createElementBlock as _createElementBlock, defineComponent } from 'vue'
const _hoisted_1 = {
  xmlns: 'http://www.w3.org/2000/svg',
  'xmlns:xlink': 'http://www.w3.org/1999/xlink',
  viewBox: '0 0 512 512'
}
const _hoisted_2 = /*#__PURE__*/ _createElementVNode(
  'rect',
  {
    x: '48',
    y: '80',
    width: '416',
    height: '352',
    rx: '48',
    ry: '48',
    fill: 'none',
    stroke: 'currentColor',
    'stroke-linejoin': 'round',
    'stroke-width': '32'
  },
  null,
  -1
)
const _hoisted_3 = /*#__PURE__*/ _createElementVNode(
  'circle',
  {
    cx: '336',
    cy: '176',
    r: '32',
    fill: 'none',
    stroke: 'currentColor',
    'stroke-miterlimit': '10',
    'stroke-width': '32'
  },
  null,
  -1
)
const _hoisted_4 = /*#__PURE__*/ _createElementVNode(
  'path',
  {
    d: 'M304 335.79l-90.66-90.49a32 32 0 0 0-43.87-.18L48 352m224 80l107.89-106.72a32 32 0 0 1 44.07-.19L464 368',
    fill: 'none',
    stroke: 'currentColor',
    'stroke-linecap': 'round',
    'stroke-linejoin': 'round',
    'stroke-width': '32'
  },
  null,
  -1
)
const _hoisted_5 = [_hoisted_2, _hoisted_3, _hoisted_4]
export default defineComponent({
  name: 'ImageOutline',
  render: function render(_ctx, _cache) {
    return _openBlock(), _createElementBlock('svg', _hoisted_1, _hoisted_5)
  }
})
