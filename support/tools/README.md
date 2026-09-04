# 维护工具

这里存放诊断工具、`scripts/` 服务管理脚本及 `migration/` 一次性迁移记录。迁移脚本依赖当时目录结构，不能在已完成迁移的工作区重复执行。运行日志和机器配置分别放入 `support/artifacts/logs/` 与 `support/config/local/`。

## DatabaseInspector

检查 ReferenceRAG 向量数据库的表、模型、文件、分块和孤立向量统计：

```bash
dotnet run --project support/tools/DatabaseInspector -- <vectors.db 路径>
```

数据库路径必须显式传入，工具不会使用开发机上的默认绝对路径。
