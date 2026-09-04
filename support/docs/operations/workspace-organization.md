# 工作区整理记录

日期：2026-09-03。

## 完成内容

- 根目录保留 Host、Business、Infrastructure、tests、support、工具隐藏目录和必要入口文件。
- 工具、配置、部署文件、文档、图片、技能、发布包、日志、测试目录、仓库内 data/models 均归入 support。
- 统一解决方案入口为 ReferenceRAG.slnx；原 .sln 及 .slnLaunch.user 归入本机 legacy 目录。
- 两个宿主、发布配置、CI、服务脚本、README 和文档链接已更新路径。
- 清除旧 dashboard-vue 的 node_modules/dist，以及旧 Core、Storage、Service、Desktop 的 bin/obj。
- 未分类 resource、旧 src 残留和前端 .env 等保留在 support/artifacts/legacy；旧发布包保留在 support/artifacts/publish/legacy。

## 配置与数据

正式配置移至 support/config/local，内容未改写。当前正式数据和模型配置指向仓库外绝对路径，未移动或修改这些外部目录。根目录内的 data 和 models 检查时没有文件，目录已移到 support。

config/local、artifacts、data、models 均受 Git 忽略规则保护。原私有文档忽略规则已迁移。AI 工具和 IDE 的隐藏目录继续保留，未清除会话或索引。

## 验证

- ReferenceRAG.slnx Release 构建通过。
- WebHost 和 DesktopHost 的 win-x64 发布配置执行成功，输出位于 support/artifacts/publish/web 和 desktop。
- DatabaseInspector 构建通过。
- 既有 NuGet 漏洞及编译器警告仍存在，本次未升级依赖。
- 本机 docker 命令映射为 WSLC，不支持 compose，Compose 校验未能运行；未执行容器部署或服务安装。
- 未启动真实知识库索引，未对外部数据进行重建或清理。

迁移和整理仍为工作区变更，本轮未执行 Git 提交或推送。

## GPU 启动优化

2026-09-04 将本地 ONNX Embedding 与 Reranker 改为首次推理时创建 CUDA Session。读取状态、初始化 BM25 和打开桌面窗口不会加载模型。显存管理器回收后，下次推理会重新按需创建 Session。

AutoIndexService 和 StartupSyncService 现在分别遵循 `autoIndexEnabled`、`syncOnStartup`。当前本机配置关闭 `syncOnStartup`，避免打开程序时因来源文件变化立即执行 GPU 向量化；实时自动索引仍保持启用。

隔离配置保留两套 CUDA 设置但关闭索引任务，实际启动健康，进程内存约 88 MB，Embedding/Reranker 加载日志为 0。15 项延迟加载及显存管理测试通过，Release 解决方案构建通过。
