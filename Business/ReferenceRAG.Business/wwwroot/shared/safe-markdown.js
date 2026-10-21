import { marked } from '../vendor/marked.js';

const tags = new Set('P BR HR H1 H2 H3 H4 H5 H6 STRONG EM DEL S PRE CODE BLOCKQUOTE UL OL LI TABLE THEAD TBODY TR TH TD A SPAN DIV DETAILS SUMMARY'.split(' '));

export function balanceTags(raw) {
  if (!raw) return '';
  let balanced = raw;

  // 1. 若在思考块中紧接着出现工具调用或工具响应，在工具前主动闭合思考块
  balanced = balanced.replace(/(<think\b[^>]*>(?:(?!<\/think>)[\s\S])*?)(?=<tool_call\b|<tool_response\b|> 🔧 \*\*已)/gi, '$1\n</think>\n\n');
  balanced = balanced.replace(/(<thought\b[^>]*>(?:(?!<\/thought>)[\s\S])*?)(?=<tool_call\b|<tool_response\b|> 🔧 \*\*已)/gi, '$1\n</thought>\n\n');

  // 2. 若上一轮思考未闭合即紧接着开始新的一轮思考，在前一轮末尾插入 </think> 避免跨阶段嵌套
  balanced = balanced.replace(/(<think\b[^>]*>(?:(?!<\/think>)[\s\S])*?)(?=<think\b)/gi, '$1\n</think>\n');
  balanced = balanced.replace(/(<thought\b[^>]*>(?:(?!<\/thought>)[\s\S])*?)(?=<thought\b)/gi, '$1\n</thought>\n');

  // 3. 智能正文隔离：若思考块中未闭合，但出现了思考向最终正文过渡的明显界标（双换行后紧跟正文问候/标题/常用段落开头），
  //    主动在此处闭合思考块，杜绝最终生成的正文回答被错误吞入折叠卡片导致页面空白
  const thoughtAnswerBoundary = /(<(?:think|thought)\b[^>]*>[\s\S]*?\n\n)(?=(?:(?:[#>\-*]|\d+\.)\s|您好|你好|好的|收到|抱歉|对不起|根据|总的来说|综上所述|Hello|Hi|Dear|Sure|Here\b))/i;
  if (thoughtAnswerBoundary.test(balanced)) {
    balanced = balanced.replace(thoughtAnswerBoundary, '$1</think>\n\n');
  }

  // 4. 统计未闭合的开标签数量，配对补齐闭合标签（循环补齐差额）
  const thinkOpen = (balanced.match(/<think\b/gi) || []).length;
  const thinkClose = (balanced.match(/<\/think>/gi) || []).length;
  for (let i = 0; i < thinkOpen - thinkClose; i++) {
    balanced += '</think>';
  }

  const thoughtOpen = (balanced.match(/<thought\b/gi) || []).length;
  const thoughtClose = (balanced.match(/<\/thought>/gi) || []).length;
  for (let i = 0; i < thoughtOpen - thoughtClose; i++) {
    balanced += '</thought>';
  }

  return balanced;
}

export function getDirectThoughtContent(el) {
  let text = '';
  for (const node of el.childNodes) {
    if (node.nodeType === 3 /* Node.TEXT_NODE */) {
      text += node.nodeValue;
    } else if (node.nodeType === 1 /* Node.ELEMENT_NODE */) {
      const tag = node.tagName.toUpperCase();
      if (tag !== 'THINK' && tag !== 'THOUGHT') {
        text += node.textContent;
      }
    }
  }
  return text.trim();
}

export function renderMarkdown(text) {
  const isStreamingThought = /<think>(?![\s\S]*<\/think>)/i.test(text || '') || /<thought>(?![\s\S]*<\/thought>)/i.test(text || '');
  const balanced = balanceTags(text || '');
  const template = document.createElement('template');
  template.innerHTML = marked.parse(balanced);

  // 思考过程聚合器（Thought Fusion Strategy）：
  // 在 Agent 模式多轮工具调用推理中，将分散的多段 <think> 标签聚合为单一优雅的思考面板，
  // 杜绝多个折叠卡片垂直堆叠，并按推理阶段结构化展现内部思维链（仅提取自身直接文本节点，防止阶梯式子元素文本重复包含）。
  const thoughtElements = [...template.content.querySelectorAll('think, thought')];
  if (thoughtElements.length > 0) {
    const thoughtSteps = thoughtElements
      .map(el => getDirectThoughtContent(el))
      .filter(Boolean);

    if (thoughtSteps.length === 0) {
      for (const el of thoughtElements) {
        if (el.parentElement && el.parentElement.tagName.toUpperCase() === 'P' &&
            el.parentElement.textContent.trim() === el.textContent.trim()) {
          el.parentElement.remove();
        } else if (el.parentElement) {
          el.remove();
        }
      }
    } else {
      const details = document.createElement('details');
      details.className = 'chat-thought-container';
      if (isStreamingThought) {
        details.setAttribute('open', '');
      }

      const summary = document.createElement('summary');
      summary.className = 'chat-thought-summary';
      if (thoughtSteps.length === 1) {
        summary.textContent = isStreamingThought ? '💭 深度思考中...' : '💭 思考过程 (点击展开)';
      } else {
        summary.textContent = isStreamingThought
          ? `💭 深度思考中 (第 ${thoughtSteps.length} 轮 Agent 推理)...`
          : `💭 思考过程 (共 ${thoughtSteps.length} 轮 Agent 思考与推理，点击展开)`;
      }

      const content = document.createElement('div');
      content.className = 'chat-thought-content';

      if (thoughtSteps.length === 1) {
        content.textContent = thoughtSteps[0];
      } else {
        for (let i = 0; i < thoughtSteps.length; i++) {
          const stepDiv = document.createElement('div');
          stepDiv.className = 'chat-thought-step';
          const isLast = i === thoughtSteps.length - 1;
          const header = document.createElement('div');
          header.className = 'chat-thought-step-header';
          header.textContent = `🔹 思考阶段 #${i + 1}${isLast ? ' (最终生成决策)' : ' (工具调用分析)'}`;
          const body = document.createElement('div');
          body.className = 'chat-thought-step-body';
          body.textContent = thoughtSteps[i];
          stepDiv.appendChild(header);
          stepDiv.appendChild(body);
          content.appendChild(stepDiv);
        }
      }

      details.appendChild(summary);
      details.appendChild(content);

      let targetNode = thoughtElements[0];
      if (targetNode.parentElement && targetNode.parentElement.tagName.toUpperCase() === 'P' &&
          targetNode.parentElement.textContent.trim() === targetNode.textContent.trim()) {
        targetNode = targetNode.parentElement;
      }
      targetNode.replaceWith(details);

      for (let i = 1; i < thoughtElements.length; i++) {
        const el = thoughtElements[i];
        if (el.parentElement && el.parentElement.tagName.toUpperCase() === 'P' &&
            el.parentElement.textContent.trim() === el.textContent.trim()) {
          el.parentElement.remove();
        } else if (el.parentElement) {
          el.remove();
        }
      }
    }
  }

  for (const element of [...template.content.querySelectorAll('*')]) {
    const tagName = element.tagName.toUpperCase();

    if (!tags.has(tagName)) {
      const elText = element.textContent || '';
      if (elText.trim()) {
        const pre = document.createElement('pre');
        const code = document.createElement('code');
        code.textContent = elText;
        pre.appendChild(code);
        element.replaceWith(pre);
      } else {
        element.remove();
      }
      continue;
    }

    for (const attr of [...element.attributes]) {
      if (tagName === 'A' && attr.name === 'href') {
        try {
          const uri = new URL(attr.value, location.href);
          if (!['http:', 'https:', 'mailto:', 'obsidian:'].includes(uri.protocol)) {
            element.removeAttribute(attr.name);
          }
        } catch {
          element.removeAttribute(attr.name);
        }
      } else if (attr.name !== 'title' && attr.name !== 'class' && attr.name !== 'open') {
        element.removeAttribute(attr.name);
      }
    }

    if (tagName === 'A') {
      element.setAttribute('target', '_blank');
      element.setAttribute('rel', 'noopener noreferrer');
    }
  }

  return template.innerHTML;
}
