using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 文本增强接口
/// </summary>
public interface ITextEnhancer
{
    /// <summary>
    /// 增强文本内容
    /// </summary>
    string Enhance(ChunkRecord chunk, FileRecord file);
}
