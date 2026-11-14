import { defineComponent, h, computed } from 'vue';
import { NCard, NEmpty, NSpin } from 'naive-ui';

/**
 * 单元格适配器：复用 naive-ui 列定义里的 render(row, index)（返回字符串/VNode/数组），
 * 使移动卡片与桌面表格共享同一套渲染逻辑与行级操作，避免重复实现（适配器模式）。
 * 独立组件令每个单元格拥有自己的响应式作用域：下载进度等局部更新只重渲染该单元格。
 */
const MobileCardCell = defineComponent({
  name: 'MobileCardCell',
  props: {
    column: { type: Object, required: true },
    row: { type: Object, required: true },
    index: { type: Number, default: 0 }
  },
  setup(props) {
    return () => {
      const { column, row, index } = props;
      let out;
      if (typeof column.render === 'function') {
        out = column.render(row, index);
      } else {
        const v = row[column.key];
        out = (v === null || v === undefined || v === '') ? '—' : v;
      }
      return (out === null || out === undefined) ? '—' : out;
    };
  }
});

/**
 * 移动态卡片列表（P2-3）：把 n-data-table 的列定义适配为纵向卡片，
 * 375px 无横向滚动即可读全字段并触达行级操作。桌面态仍用 n-data-table（v-else）。
 * props 与 n-data-table 对齐：columns / data / rowKey / loading / rowProps，
 * 另加 titleKey（作为卡片标题的列）、actionKey（作为底部操作区的列，默认 'actions'）。
 */
export const MobileCardList = defineComponent({
  name: 'MobileCardList',
  props: {
    columns: { type: Array, default: () => [] },
    data: { type: Array, default: () => [] },
    rowKey: { type: [String, Function], default: '' },
    loading: { type: Boolean, default: false },
    rowProps: { type: Function, default: null },
    titleKey: { type: String, default: '' },
    actionKey: { type: String, default: 'actions' },
    emptyText: { type: String, default: '暂无数据' }
  },
  setup(props) {
    const cols = computed(() => props.columns.filter(c => c && c.key && c.type !== 'selection' && c.type !== 'expand'));
    const keyOf = (row, i) => {
      if (typeof props.rowKey === 'function') return props.rowKey(row);
      if (props.rowKey && row[props.rowKey] != null) return row[props.rowKey];
      return i;
    };
    const labelOf = (col) => (typeof col.title === 'function' ? col.title() : (col.title || col.key));

    return () => {
      if (!props.data.length) {
        return h(NSpin, { show: props.loading, size: 'small' }, {
          default: () => h(NEmpty, { description: props.emptyText, size: 'small', style: 'padding: 28px 0' })
        });
      }
      const titleCol = props.titleKey ? cols.value.find(c => c.key === props.titleKey) : null;
      const actionCol = props.actionKey ? cols.value.find(c => c.key === props.actionKey) : null;
      const fieldCols = cols.value.filter(c => c !== titleCol && c !== actionCol);

      const cards = props.data.map((row, i) => {
        const extra = typeof props.rowProps === 'function' ? (props.rowProps(row, i) || {}) : {};
        return h(NCard, {
          key: keyOf(row, i),
          size: 'small',
          bordered: true,
          class: 'mobile-card-item',
          contentStyle: 'padding: 12px 14px;',
          ...extra
        }, {
          default: () => [
            titleCol ? h('div', { class: 'mobile-card-title' }, h(MobileCardCell, { column: titleCol, row, index: i })) : null,
            fieldCols.length ? h('div', { class: 'mobile-card-fields' }, fieldCols.map(col =>
              h('div', { class: 'mobile-card-field', key: col.key }, [
                h('span', { class: 'mobile-card-label' }, labelOf(col)),
                h('span', { class: 'mobile-card-value' }, h(MobileCardCell, { column: col, row, index: i }))
              ])
            )) : null,
            actionCol ? h('div', { class: 'mobile-card-actions' }, h(MobileCardCell, { column: actionCol, row, index: i })) : null
          ]
        });
      });

      return h(NSpin, { show: props.loading, size: 'small' }, {
        default: () => h('div', { class: 'mobile-card-list' }, cards)
      });
    };
  }
});

export default MobileCardList;