# ReferenceRAG Agent 指南

本文档供各类 AI Agent（如 Claude, Antigravity, Copilot 等）快速理解本项目的核心目录结构、代码位置以及常见任务执行规范。

---

## 1. 前端代码位置 (Frontend Path)

> [!IMPORTANT]
> 本项目的前端代码**并非**存放在根目录常见的 `frontend/`、`web/` 或 `ui/` 目录下，而是作为嵌入静态资源存放在业务库项目下：
> 
> - **相对路径**：`Business/ReferenceRAG.Business/wwwroot`
> - **绝对路径**：`E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot`

### 前端技术栈与架构
- **技术栈**：原生 ES Modules (ESM) + Vue 3 + Naive UI。
- **免构建设计**：无需 Webpack/Vite 繁重编译打包，现代浏览器通过 `index.html` 中的 `<script type="importmap">` 直接解析原生 ESM 模块，依赖全部放置于 `vendor/` 目录下。
- **与后端的集成**：
  - `Business/ReferenceRAG.Business/ReferenceRAG.Business.csproj` 将 `wwwroot/**/*` 打包为嵌入式资源（`EmbeddedResource`）。
  - `Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj` 在构建和发布阶段将其自动链接并拷贝到宿主程序的 `wwwroot` 输出目录下，由 ASP.NET Core 静态文件中间件提供宿主服务。

---

## 2. 前端目录结构详解

前端源码位于 `Business/ReferenceRAG.Business/wwwroot/`，内部组织如下：

```
Business/ReferenceRAG.Business/wwwroot/
├── index.html               # 前端入口 HTML 与 importmap 依赖映射配置
├── app.js                   # 前端挂载与启动入口
├── check.mjs                # AST 符号绑定、模板检查与导出作用域验证门禁脚本
├── package.json             # 前端测试与检查脚本定义
├── app/                     # 核心应用代码
│   ├── App.js               # 根组件
│   ├── api/                 # 统一 API 客户端与请求封装 (index.js 等)
│   ├── components/          # 业务复用组件 (FolderPicker, SourceFileViewer, VectorMaintenance 等)
│   ├── config/              # 环境配置 (env.js)
│   ├── layouts/             # 页面布局组件 (MainLayout.js)
│   ├── router/              # 前端路由定义 (index.js)
│   ├── stores/              # 全局状态管理 (auth.js, index.js)
│   └── views/               # 页面视图
│       ├── Dashboard.js     # 仪表盘
│       ├── Chat.js          # AI 对话界面
│       ├── Search.js        # 检索测试
│       ├── Sources.js       # 知识源配置与管理
│       ├── Models.js        # 模型生命周期与管理
│       ├── Graph.js         # 知识图谱视图
│       ├── BM25Index.js     # BM25 索引管理
│       ├── Performance.js   # 性能监控与耗时追踪
│       ├── ApiKeys.js       # API Key 授权管理
│       ├── ApiHelp.js       # API 帮助文档
│       ├── Settings.js      # 系统设置
│       ├── System.js        # 系统状态
│       ├── Logs.js          # 系统日志
│       └── Login.js         # 登录界面
├── core/                    # 核心客户端逻辑与工具库
├── shared/                  # 共享组件与工具方法
├── styles/                  # 样式文件 (CSS)
├── tests/                   # 前端自动化单元测试套件 (*.test.mjs)
└── vendor/                  # 本地托管的第三方依赖库 (Vue, Naive UI, Axios, Marked, 字体图标等)
```

---

## 3. 前端常用命令与门禁校验

在修改前端代码后，必须在根目录或前端目录下运行以下门禁以确保质量：

### 1) 语法与模板符号静态检查 (AST 检查)
```bash
# 在项目根目录下运行：
node Business/ReferenceRAG.Business/wwwroot/check.mjs

# 或进入前端目录运行：
cd Business/ReferenceRAG.Business/wwwroot
npm run check
```

### 2) 前端自动化单元测试
```bash
# 在项目根目录下运行：
node --test Business/ReferenceRAG.Business/wwwroot/tests/*.test.mjs

# 或进入前端目录运行：
cd Business/ReferenceRAG.Business/wwwroot
npm test
```

---

## 4. 后端及项目整体结构速览

- **解决方案**：`ReferenceRAG.slnx`（.NET 10.0）
- **Host 层**：
  - `Host/ReferenceRAG.WebHost`：ASP.NET Core Web API 宿主，提供 HTTP/REST API、MCP 工具、SignalR Hub、桌面 IPC 与静态文件托管。
- **业务层**：
  - `Business/ReferenceRAG.Business`：检索调度、索引管道、分块逻辑、图谱关联、聊天工作流，以及前端静态资源（`wwwroot/`）。
- **基础设施层**：
  - `Infrastructure/ReferenceRAG.Infrastructure`：SQLite 存储、向量引擎、BM25 检索、ONNX/GGUF (LLamaSharp) 双引擎推理、显存生命周期调度。
- **测试工程**：
  - `tests/`：包含全链路架构门禁、检索质量测试及性能压测用例。
