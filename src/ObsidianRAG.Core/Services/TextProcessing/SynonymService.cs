namespace ObsidianRAG.Core.Services;

/// <summary>
/// 同义词服务 - 提供查询同义词扩展
/// </summary>
public class SynonymService
{
    private readonly Dictionary<string, List<string>> _synonymGroups;
    private readonly HashSet<string> _stopWords;

    public SynonymService()
    {
        _synonymGroups = BuildSynonymDictionary();
        _stopWords = new HashSet<string>
        {
            "的", "是", "在", "了", "和", "与", "或", "但", "这", "那", "的", "地", "得",
            "我", "你", "他", "她", "它", "们", "的", "啊", "吧", "呢", "吗", "呀",
            "一个", "一些", "什么", "怎么", "如何", "为什么", "因为", "所以", "如果",
            "就", "才", "都", "也", "还", "又", "很", "太", "真", "好", "对", "错"
        };
    }

    /// <summary>
    /// 扩展查询 - 添加同义词
    /// </summary>
    public string ExpandQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var words = Tokenize(query);
        var expandedWords = new List<string>();

        foreach (var word in words)
        {
            expandedWords.Add(word);

            // 添加同义词（仅对有意义的词）
            if (word.Length >= 2 && !_stopWords.Contains(word))
            {
                if (_synonymGroups.TryGetValue(word, out var synonyms))
                {
                    // 只添加最相关的1-2个同义词
                    expandedWords.AddRange(synonyms.Take(2));
                }
            }
        }

        return string.Join(" ", expandedWords);
    }

    /// <summary>
    /// 获取词的同义词列表
    /// </summary>
    public List<string> GetSynonyms(string word)
    {
        if (_synonymGroups.TryGetValue(word, out var synonyms))
        {
            return synonyms;
        }
        return new List<string>();
    }

    /// <summary>
    /// 判断是否为停用词
    /// </summary>
    public bool IsStopWord(string word)
    {
        return _stopWords.Contains(word);
    }

    /// <summary>
    /// 简单分词
    /// </summary>
    private List<string> Tokenize(string text)
    {
        return text
            .Split(new[] { ' ', '　', ',', '，', '.', '。', '!', '！', '?', '？', '\n', '\t', '\r' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();
    }

    /// <summary>
    /// 构建同义词词典
    /// </summary>
    private Dictionary<string, List<string>> BuildSynonymDictionary()
    {
        return new Dictionary<string, List<string>>
        {
            // 编程相关
            ["编程"] = new List<string> { "程序设计", "写代码", "coding" },
            ["代码"] = new List<string> { "程序", "code", "源码" },
            ["函数"] = new List<string> { "方法", "function", "method" },
            ["变量"] = new List<string> { "variable", "参量" },
            ["数组"] = new List<string> { "列表", "array", "list" },
            ["对象"] = new List<string> { "object", "实例" },
            ["类"] = new List<string> { "class", "类型" },

            // 配置相关
            ["配置"] = new List<string> { "设置", "settings", "config", "setup" },
            ["安装"] = new List<string> { "部署", "install", "setup", "搭建" },
            ["运行"] = new List<string> { "执行", "启动", "run", "execute" },
            ["停止"] = new List<string> { "关闭", "停止运行", "stop" },

            // 数据相关
            ["数据"] = new List<string> { "data", "信息" },
            ["数据库"] = new List<string> { "db", "database", "资料库" },
            ["存储"] = new List<string> { "保存", "storage", "save" },
            ["读取"] = new List<string> { "加载", "读入", "load", "read" },

            // 文件相关
            ["文件"] = new List<string> { "档案", "file", "文档" },
            ["文件夹"] = new List<string> { "目录", "folder", "directory" },
            ["路径"] = new List<string> { "path", "directory" },

            // 网络相关
            ["搜索"] = new List<string> { "查询", "检索", "search", "find" },
            ["查询"] = new List<string> { "搜索", "查找", "query", "search" },
            ["索引"] = new List<string> { "index", "建立索引" },
            ["服务器"] = new List<string> { "server", "服务端", "后端" },
            ["客户端"] = new List<string> { "client", "前端" },

            // AI/ML相关
            ["向量"] = new List<string> { "embedding", "矢量", "vector" },
            ["模型"] = new List<string> { "model", "算法" },
            ["训练"] = new List<string> { "train", "学习" },
            ["推理"] = new List<string> { "inference", "预测", "predict" },
            ["语义"] = new List<string> { "semantic", "含义", "意义" },

            // 文档相关
            ["文档"] = new List<string> { "docs", "说明书" },
            ["教程"] = new List<string> { "指南", "tutorial", "guide" },
            ["示例"] = new List<string> { "例子", "sample", "example", "demo" },
            ["说明"] = new List<string> { "解释", "文档", "documentation" },

            // 开发相关
            ["开发"] = new List<string> { "development", "研发" },
            ["测试"] = new List<string> { "testing", "test" },
            ["调试"] = new List<string> { "debug", "调试程序" },
            ["优化"] = new List<string> { "optimize", "调优" },
            ["系统"] = new List<string> { "system", "体系" },
            ["错误"] = new List<string> { "报错", "error", "bug", "异常" },

            // 项目相关
            ["项目"] = new List<string> { "project", "工程", "方案" },
            ["功能"] = new List<string> { "特性", "feature", "能力" },
            ["模块"] = new List<string> { "module", "组件", "component" },
            ["接口"] = new List<string> { "api", "interface", "端口" },

            // 系统相关
            ["系统"] = new List<string> { "system", "体系" },
            ["平台"] = new List<string> { "platform" },
            ["环境"] = new List<string> { "environment", "env" },
            ["版本"] = new List<string> { "version", "版本号" },

            // 中文常用同义词
            ["学习"] = new List<string> { "study", "掌握" },
            ["理解"] = new List<string> { "明白", "懂" },
            ["知道"] = new List<string> { "了解", "认识" },
            ["使用"] = new List<string> { "应用", "运用" },
            ["实现"] = new List<string> { "完成", "达成" },
            ["创建"] = new List<string> { "新建", "建立" },
            ["删除"] = new List<string> { "移除", "remove" },
            ["修改"] = new List<string> { "更新", "更改", "编辑" },
            ["查看"] = new List<string> { "浏览", "看" },
            ["获取"] = new List<string> { "取得", "得到" }
        };
    }
}
