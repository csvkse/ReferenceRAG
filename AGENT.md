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

---

## 5. 凭据安全红线与脱敏规范 (Security & Credential Governance)

在后续所有任务、功能开发、排障文档编写以及测试用例编写中，必须严格遵守以下凭据安全红线：

### 1) 文档规范 (docs/ 目录严禁真实凭据)
- **严禁真实密钥与密码**：编写技术方案、排障复盘（Lessons Learned）、API 调研或 curl 示例时，**绝对禁止**将真实的 OpenAI Key、平台管理员密码（如 `WEB_ADMIN_PASSWORD`）、隧道 ID、EasyTier 组网密钥 (`network_secret`) 或公网对端域名写入文档。
- **强制使用标准占位符**：
  - **Tunnel ID**：统一使用符合格式校验的虚拟占位符 `tunnel_0123456789abcdef0123456789abcdef`。
  - **API Key / Token**：使用 `sk-proj-test-secret-key-1234567890`、`sk-test-super-secret-12345` 或 `lwt_live_8f3a9e...`。
  - **管理员/系统密码**：使用 `your_secure_password_here` 或 `<ADMIN_PASSWORD>`。
  - **EasyTier 组网配置**：使用 `network_name = "demo_net"`, `network_secret = "demo_secret_token"`, `peers = ["wss://easytier-peer.example.com"]`。
  - **网络地址**：使用 RFC 5737 示例域名 `example.com` 或回环地址 `127.0.0.1`。

### 2) 测试用例规范 (tests/ 与 plugins/**/tests)
- **单元与集成测试夹具**：默认使用纯内存/本地 Mock 凭据（例如：`mockPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nMOCK_KEY_DATA\n-----END OPENSSH PRIVATE KEY-----"`）。
- **动态密码防泄露**：端到端集成测试若需生成真实密码，必须动态随机生成（例如 `[guid]::NewGuid().ToString('N')`），并在测试日志持久化时显式执行脱敏过滤（例如 `.Replace($password, '[REDACTED]')`）。

### 3) 真实密钥与环境连接加载规范 (/env/.envdev)
- **唯一合法来源**：若本地联调、端到端冒烟测试或远程诊断确需使用真实密钥与宿主连接信息，必须且只能从项目根目录的 `/env/.envdev` 文件（或读取系统当前环境变量）中动态提取。
- **Git 隔离防护**：`/env/.envdev` 已被根目录 `.gitignore` 全局严格忽略（`env/`, `.envdev`, `*.envdev`, `docs_secret/`），绝对禁止将其提交到版本库中。
- **环境回退机制**：测试脚本在未检测到 `/env/.envdev` 中的特定凭据时，应设计优雅降级或跳过远程鉴权逻辑，绝不允许通过在代码中硬编码真实密钥来“临时跑通”测试。

