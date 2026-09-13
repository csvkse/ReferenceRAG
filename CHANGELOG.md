# 改动历史

本文件记录面向使用者的重要变化。完整提交记录和安装包见 [GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)。

## [1.0.7] - 2026-09-04

### 架构

- 将项目迁移到共享的 `Infrastructure`、`Business`、`ApiHost`、`WebHost` 和 `DesktopHost` 分层架构。
- Web 与桌面端共享业务逻辑、API 语义和同一套静态前端资源。
- 桌面端改用 InfiniFrame、WebView2 和进程内 IPC，默认不监听 HTTP 端口。
- Web 前端改为浏览器原生 ES Modules，移除 Vite、TypeScript/SFC 发布编译和 npm 构建流程。
- Web 与桌面端均使用 .NET 10 常规发布，关闭 NativeAOT 和裁剪。

### 功能与修复

- 保留 AI 对话会话状态，切换页面后可继续原会话。
- 简化在线聊天、嵌入和重排服务的配置选择，并修正线上模型配置仍回退本地的问题。
- API Key 缺失时返回可诊断的配置错误，避免在服务构造阶段因空密钥崩溃。
- 修正桌面窗口初始尺寸与屏幕可用区域适配。
- 正常处理后台清理服务在应用退出时收到的取消信号。
- 增加 GPU 内存收缩回调和无冲突释放机制，降低启动及模型切换期间的显存占用。
- 增加孤儿数据后台清理服务。

### 工程与发布

- 将工具、配置模板、部署文件、文档和生成产物统一归入 `support/`。
- 保留迁移前代码于 `Old` 分支。
- 修正 GitHub Actions 在 Windows PowerShell 中执行前端测试时的通配符兼容问题。
- 发布 Windows x64 自包含桌面包 `ReferenceRAG-win-x64.zip`。

### 升级提示

- 本机配置迁移到 `support/config/local/`；发布环境仍从发布目录读取配置。
- 可通过 `REFERENCERAG_CONTENT_ROOT` 指定配置和相对数据路径的根目录。
- 禁止新旧程序同时写入同一个数据目录；迁移不会自动移动真实数据和模型。

## 1.0.6 及更早版本

- 提供 AI 对话、向量/BM25/图谱混合检索、可选重排、自动索引、模型管理、MCP、Skill、REST API 和 Swagger。
- 支持本地 ONNX Embedding/Reranker、CUDA 加速及 OpenAI 兼容 API 模式。
- 历史版本详情见 [Releases](https://github.com/csvkse/ReferenceRAG/releases)。

[1.0.7]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.7
