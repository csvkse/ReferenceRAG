/**
 * SearchHitAdapter: 搜索命中项与阅读器跳转适配器 (Adapter Pattern)
 * 统一 RAG 向量切片与物理文件正则/字串匹配异构数据为标准 ReaderNavigationTarget
 */

export class SearchHitAdapter {
    /**
     * 将 RAG 向量/混合检索切片转换为统一阅读导航对象
     * @param {Object} chunk - RAG Chunk 对象
     * @param {string} query - 搜索关键词
     * @returns {Object} ReaderNavigationTarget
     */
    static fromRagChunk(chunk, query = '') {
        const filePath = (chunk?.filePath || chunk?.title || '').replace(/\\/g, '/');
        const startLine = Number(chunk?.startLine) > 0 ? Number(chunk.startLine) : 1;
        const endLine = Number(chunk?.endLine) >= startLine ? Number(chunk.endLine) : startLine;

        return {
            sourceName: chunk?.source || '',
            filePath: filePath,
            fileName: filePath.split('/').pop() || '未命名文档',
            targetLine: startLine,
            endLine: endLine,
            highlightPattern: query || '',
            isRegex: false,
            previewSnippet: chunk?.content || ''
        };
    }

    /**
     * 将文件搜索 (FileSearch) 的 Hunk 匹配行转换为统一阅读导航对象
     * @param {Object} file - FileSearchMatchGroup
     * @param {Object} line - SearchMatchLine
     * @param {string} query - 搜索模式
     * @param {boolean} isRegex - 是否正则
     * @returns {Object} ReaderNavigationTarget
     */
    static fromFileMatch(file, line, query = '', isRegex = false) {
        const filePath = (file?.fullPath || file?.relativePath || '').replace(/\\/g, '/');
        const lineNumber = Number(line?.lineNumber) > 0 ? Number(line.lineNumber) : 1;

        return {
            sourceName: file?.sourceName || '',
            filePath: filePath,
            fileName: (file?.relativePath || filePath).split('/').pop() || '未命名文件',
            targetLine: lineNumber,
            endLine: lineNumber,
            highlightPattern: query || '',
            isRegex: Boolean(isRegex),
            previewSnippet: line?.text || ''
        };
    }

    /**
     * 生成 Vue Router 跳转所需的 Location 对象
     * @param {Object} target - ReaderNavigationTarget
     * @returns {Object} vue-router push 参数
     */
    static toRouteLocation(target) {
        return {
            name: 'Reader',
            query: {
                source: target.sourceName || '',
                path: target.filePath,
                line: String(target.targetLine || 1),
                highlight: target.highlightPattern || '',
                isRegex: target.isRegex ? '1' : '0'
            },
            hash: `#L${target.targetLine || 1}`
        };
    }
}
