# 维护工具

这里存放可复用的诊断和维护工具，不放置一次性脚本、运行日志或机器相关配置。

## DatabaseInspector

检查 ReferenceRAG 向量数据库的表、模型、文件、分块和孤立向量统计：

```bash
dotnet run --project tools/DatabaseInspector -- <vectors.db 路径>
```

数据库路径必须显式传入，工具不会使用开发机上的默认绝对路径。
