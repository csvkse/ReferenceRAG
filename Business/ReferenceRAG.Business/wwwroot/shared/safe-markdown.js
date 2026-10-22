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

  // 3. 智能正文隔离：仅当开标签数多于闭标签数（确实存在未闭合的思考块）时才尝试隔离
  //    使用负向预查禁止跨越已闭合的 </think>，且仅在遇到显著的自然语言正文起首词时闭合，
  //    严禁将常规 Markdown 列表符号（如 -、*、1.）作为判定边界，防止思考链内部的条目被误截断。
  let thinkOpen = (balanced.match(/<think\b/gi) || []).length;
  let thinkClose = (balanced.match(/<\/think>/gi) || []).length;
  if (thinkOpen > thinkClose) {
    const thoughtAnswerBoundary = /(<think\b[^>]*>(?:(?!<\/think>)[\s\S])*?\n\n)(?=(?:您好|你好|好的|收到|抱歉|对不起|根据|总的来说|综上所述|Hello|Hi|Dear|Sure|Here\b))/i;
    if (thoughtAnswerBoundary.test(balanced)) {
      balanced = balanced.replace(thoughtAnswerBoundary, '$1</think>\n\n');
    }
  }

  let thoughtOpen = (balanced.match(/<thought\b/gi) || []).length;
  let thoughtClose = (balanced.match(/<\/thought>/gi) || []).length;
  if (thoughtOpen > thoughtClose) {
    const thoughtAnswerBoundary = /(<thought\b[^>]*>(?:(?!<\/thought>)[\s\S])*?\n\n)(?=(?:您好|你好|好的|收到|抱歉|对不起|根据|总的来说|综上所述|Hello|Hi|Dear|Sure|Here\b))/i;
    if (thoughtAnswerBoundary.test(balanced)) {
      balanced = balanced.replace(thoughtAnswerBoundary, '$1</thought>\n\n');
    }
  }

  // 4. 统计未闭合的开标签数量，配对补齐闭合标签（循环补齐差额）
  thinkOpen = (balanced.match(/<think\b/gi) || []).length;
  thinkClose = (balanced.match(/<\/think>/gi) || []).length;
  for (let i = 0; i < thinkOpen - thinkClose; i++) {
    balanced += '</think>';
  }

  thoughtOpen = (balanced.match(/<thought\b/gi) || []).length;
  thoughtClose = (balanced.match(/<\/thought>/gi) || []).length;
  for (let i = 0; i < thoughtOpen - thoughtClose; i++) {
    balanced += '</thought>';
  }

  return balanced;
}

export function extractThoughts(rawText) {
  if (!rawText) return { thoughtSteps: [], cleanBody: '', isStreaming: false };

  const isStreaming = /<think\b(?![\s\S]*<\/think>)/i.test(rawText) || /<thought\b(?![\s\S]*<\/thought>)/i.test(rawText);
  const balanced = balanceTags(rawText);

  const thoughtSteps = [];
  const cleanBody = balanced.replace(/<(think|thought)\b[^>]*>([\s\S]*?)<\/\1>/gi, (match, tag, content) => {
    const trimmed = content.trim();
    if (trimmed) {
      thoughtSteps.push(trimmed);
    }
    return '';
  }).trimStart();

  return {
    thoughtSteps,
    cleanBody,
    isStreaming
  };
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
  const { thoughtSteps, cleanBody, isStreaming } = extractThoughts(text || '');
  const template = document.createElement('template');
  template.innerHTML = cleanBody ? marked.parse(cleanBody) : '';

  // 思考过程聚合器（Thought Fusion Strategy）：
  // 在 Agent 模式多轮工具调用推理或深度思考模型生成中，将思考内容与正文从 AST/解析前置层面彻底隔离，
  // 杜绝将自定义 <think> 标签直接作为 HTML 喂给 Markdown 解析器导致的 HTML5 特殊元素解析丢弃与 DOM 树吞噬。
  if (thoughtSteps.length > 0) {
    const details = document.createElement('details');
    details.className = 'chat-thought-container';
    if (isStreaming) {
      details.setAttribute('open', '');
    }

    const summary = document.createElement('summary');
    summary.className = 'chat-thought-summary';
    if (thoughtSteps.length === 1) {
      summary.textContent = isStreaming ? '💭 深度思考中...' : '💭 思考过程 (点击展开)';
    } else {
      summary.textContent = isStreaming
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
    template.content.prepend(details);
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
