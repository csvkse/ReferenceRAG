# 配套目录

| 目录 | 用途 | Git |
|---|---|---|
| tools | 诊断、开发、服务管理及历史迁移工具 | 提交 |
| config/examples | 不含本机秘密的配置模板 | 提交 |
| config/local | 开发机配置和容器配置 | 忽略 |
| deploy/docker | Dockerfile 与 Compose | 提交 |
| docs | 架构、运维、迁移说明及图片 | 提交；原有私有文档目录继续忽略 |
| skill | 对外提供的 Agent 技能 | 提交 |
| artifacts/publish | 双端发布输出和旧发布包 | 忽略 |
| artifacts/logs | 构建及运行日志 | 忽略 |
| artifacts/test-runs | 隔离测试目录 | 忽略 |
| artifacts/legacy | 未确认可删除的旧本机文件 | 忽略 |
| data、models | 仓库内运行数据、模型 | 忽略 |

## 配置和数据

宿主构建从 `support/config/local/appsettings*.json` 复制配置。已有配置保持原值；仓库外绝对数据路径不会随整理变更。发布程序默认以发布目录为配置根目录，也可设置 `REFERENCERAG_CONTENT_ROOT`。

新环境先参考 `config/examples/` 创建本机配置，明确指定数据和模型路径。目录存在不代表应用自动使用它；请以 `dataPath`、`modelsRootPath` 实际配置为准。`config/local` 中的内容不能提交到 Git。

## 发布

在仓库根目录执行：

```powershell
dotnet publish Host/ReferenceRAG.WebHost -p:PublishProfile=win-x64
dotnet publish Host/ReferenceRAG.DesktopHost -p:PublishProfile=win-x64
```

输出分别位于 `support/artifacts/publish/web` 和 `desktop`。

Docker 使用 `docker compose -f support/deploy/docker/compose.yml up --build`。启动前将 Docker 模板复制到 `support/config/local/docker/appsettings.Production.json` 并配置模型；该文件映射为容器的 `/app/appsettings.json`，不会遮蔽程序目录。此处记录运行方式，不代表已经执行部署。

## 清理边界

旧前端 `node_modules/dist` 和旧四个工程 `bin/obj` 已清除。旧发布目录、`.env`、快捷方式及未分类本机文件保存在 `artifacts/legacy` 或 `artifacts/publish/legacy`。它们不受 Git 保护，后续删除前需要核对独有配置和数据。

迁移测试目录按历史原样归档，其中绝对路径可能指向整理前的位置，不能直接启动；需要先生成或调整隔离配置。不要使用真实数据做清理验证。

AI 工具与 IDE 的隐藏目录保留原位置，以保持自动发现和现有工作状态。
