# Web 模式文件夹选择器架构设计与实施方案

## 1. 概述与背景

### 1.1 现状与痛点
ReferenceRAG 拥有桌面端（基于 InfiniFrame / WebView2 的 DesktopHost）与 Web 端（基于 ASP.NET Core 的 WebHost / Docker 容器部署）两种主要运行形态。
在现有的知识库数据源添加（`Sources.js`）及配置管理（`Settings.js`）场景中：
- **桌面端模式 (`isDesktop == true`)**：前端可以通过 IPC 调用后端的 `POST /api/platform/select-folder`，调用本地操作系统的 Win32 原生文件浏览对话框（`ShowOpenFolderAsync`），使用体验流畅。
- **Web 模式 (`isDesktop == false`)**：例如在 Docker 容器（Linux）、远程虚拟机或局域网服务器运行并通过浏览器访问时：
  1. `selectDirectory` 按钮在前端被硬编码隐藏（`v-if="isDesktop"`）；
  2. 即使强行触发，后端 `BrowserPlatformActions.CanSelectFolder` 也固定返回 `false`，接口返回 HTTP 501 `请填写服务器目录`；
  3. 用户必须手动在输入框中逐字敲入或粘贴服务器上的绝对路径（如 `/app/data/my-notes` 或 `D:/WorkProject/Knowledge`）；
  4. 绝大多数用户对服务器/容器内部目录结构不熟悉，容易输入错误路径，且无法可视化查看目录下的子文件夹，导致配置门槛高、易出错。

### 1.2 目标
参考成熟的 Linux 运维工具项目 `E:\WorkProject\CSharp\PrivateProject\Web\WebToolForLinux` 中的文件浏览器设计，在 ReferenceRAG 中引入**轻量、高效、跨平台且具备严密安全防线的 Web 文件夹选择器（Web Folder Picker）**：
1. **后端**：提供跨平台文件系统导航 API（`GET /api/filesystem/browse` 及目录新建 `POST /api/filesystem/mkdir`），自动兼容 Windows 物理磁盘盘符列表（`C:/`、`D:/`...）与 Linux 根目录挂载（`/`、`/app`、`/mnt`...），具备完备的路径正规化、防目录穿越（Path Traversal）与系统权限异常捕获。
2. **前端**：基于 Naive UI 与 Vue 3 开发轻量级、响应式模态框组件 `FolderPickerModal.js`，支持面包屑直达、上一级、目录穿透下钻、排序、新建文件夹、快速选中当前目录，并自动联动填入数据源表单与数据源名称。
3. **架构整合**：统一桌面端与 Web 端的文件夹添加体验。在 Web 模式下点击“选择文件夹”无缝唤起 Web 选择器弹窗；桌面端在有需要时亦可复用。

---

## 2. 参考项目（WebToolForLinux）方案调研与技术借鉴

在调研 `E:\WorkProject\CSharp\PrivateProject\Web\WebToolForLinux` 的具体实现后，提炼出以下核心优秀设计模式：

### 2.1 路径正规化与安全内核 (`FileBrowserPath.cs`)
- **跨平台路径正规化**：
  - Windows 环境下：当请求根路径为空或 `"/"` 时，识别为“虚拟根”，通过 `DriveInfo.GetDrives()` 列举所有就绪的物理盘符（`C:/`、`D:/` 等）；支持盘符绝对路径（如 `D:/Knowledge`）。
  - Linux / Docker 环境下：以根目录 `/` 为起点，各级路径规范为标准 Unix 风格斜杠 `/`。
- **严格的安全边界防御**：
  - 禁止包含 `..` 等可能逃逸的目录穿越序列；
  - 过滤 Windows 控制字符、设备保留字（`CON`、`PRN`、`AUX`、`NUL`、`COM1-9`、`LPT1-9`）及备用数据流冒号 `:`；
  - 区分 Windows 大小写不敏感（`OrdinalIgnoreCase`）与 Linux 大小写敏感（`Ordinal`）比较。
- **父级与名称推导**：
  - `Parent(path)` 算法：当处于盘符根（如 `C:/`）时返回虚拟根 `"/"`；处于虚拟根时返回 `null`；标准目录返回上级全路径。
  - `Name(path)` 算法：提取当前层级的目录展示名称。

### 2.2 前端交互与导航体验 (`FilePicker.js` & `filePaths.js`)
- **面包屑导航 (`buildFileBreadcrumbs`)**：将 `/d:/data/notes` 或 `/app/data/notes` 拆解为可单点直达的面包屑节点集合，支持点击任意层级瞬时跳转。
- **层级穿透与快速选择**：
  - 双击或单击行：进入子文件夹；
  - 快捷按钮：“⬆ 上一级”、“刷新”、“选择当前文件夹”；
  - 实时显示当前选中目录与加载中状态；
  - 友好展示每个目录的修改时间与项目数。

---

## 3. ReferenceRAG 架构可行性与集成分析

### 3.1 后端可行性
- **技术栈一致性**：ReferenceRAG 后端基于 .NET 10 (C# 14 / ASP.NET Core)，与 WebToolForLinux 同为 .NET 体系。
- **架构解耦**：在 `Host/ReferenceRAG.ApiHost/Controllers/` 新增 `FileSystemController.cs`，依赖轻量工具类 `FileBrowserPath.cs`，不侵入现有的 RAG 向量检索、分块与任务调度内核。
- **鉴权与安全**：现有全局 `ApiKeyMiddleware` 已经自动作用于所有 `/api/*` 路由，Web 文件夹选择器的接口天然受到系统 API Key 的保护。

### 3.2 前端可行性
- **UI 风格契合**：ReferenceRAG 的前端采用纯 ESM 模块架构（无 Webpack/Vite 复杂打包），依托 Vue 3 + Naive UI。
- **组件规范适配**：
  - 新建 `Business/ReferenceRAG.Business/wwwroot/app/components/FolderPickerModal.js`；
  - 严格遵守已有的前端质量门禁（`FrontendViewIntegrityTests` 和 `check.mjs`），导出的标识符完全闭包，保证在纯浏览器端无任何未声明变量泄漏；
  - 模板中支持使用 Naive UI 原生卡片（`NModal`、`NCard`、`NButton`、`NBreadcrumb`、`NDataTable` / 自定义列表、`NSpin`、`NInput`）。
- **页面集成点**：
  - `Sources.js`：添加源卡片中，放开“选择文件夹”按钮展示条件（无论桌面端还是 Web 端均可见；Web 模式下触发 Web 文件夹选择器）。
  - 选中后自动将路径填入 `newSource.path`，若 `newSource.name` 尚为空，自动使用该文件夹的名称作为默认源名称。

---

## 4. 详细接口设计与数据契约

### 4.1 目录浏览接口
- **URL**: `GET /api/filesystem/browse`
- **Query 参数**:
  - `path` (string, 可选): 要浏览的目标路径。若留空或为 `"/"`，在 Windows 下展示磁盘列表，在 Linux 下展示根目录 `/`。
  - `folderOnly` (bool, 默认 `true`): 是否仅列出文件夹。为文件夹选择场景设计，默认隐藏普通文件以保持界面清爽。
- **响应体示例**:
```json
{
  "path": "/app/data",
  "parent": "/app",
  "name": "data",
  "isRoot": false,
  "isVirtualRoot": false,
  "entries": [
    {
      "name": "obsidian-notes",
      "path": "/app/data/obsidian-notes",
      "isDirectory": true,
      "size": 0,
      "modified": "2026-10-22T05:20:00Z"
    },
    {
      "name": "technical-docs",
      "path": "/app/data/technical-docs",
      "isDirectory": true,
      "size": 0,
      "modified": "2026-10-22T05:21:00Z"
    }
  ]
}
```

### 4.2 新建目录接口（可选辅助）
- **URL**: `POST /api/filesystem/mkdir`
- **请求体**:
```json
{
  "path": "/app/data/new-folder"
}
```
- **响应体**:
```json
{
  "success": true,
  "path": "/app/data/new-folder",
  "message": "目录创建成功"
}
```

### 4.3 异常与状态码定义
- `200 OK`: 成功获取目录信息或创建目录；
- `400 Bad Request`: 路径格式非法、包含非法控制符或目录遍历模式（如 `..`）；
- `403 Forbidden`: 系统权限不足（`UnauthorizedAccessException`），阻止由于系统权限引发的异常崩溃，返回明确文字说明；
- `404 Not Found`: 目标路径不存在或不是有效目录。

---

## 5. 前端交互与组件实现方案

### 5.1 组件结构 (`FolderPickerModal.js`)
```
+--------------------------------------------------------------+
|  选择服务器文件夹                             [刷新] [关闭✕]   |
+--------------------------------------------------------------+
|  当前路径:  /app > data > obsidian-notes                     |
|  [⬆ 返回上一级]   [新建文件夹]                                 |
+--------------------------------------------------------------+
|  📁 文件夹列表 (按字母升序)                                  |
|  ----------------------------------------------------------- |
|  📁 daily-notes                   2026-10-20 14:00           |
|  📁 projects                      2026-10-21 09:30           |
|  📁 references                    2026-10-22 01:15           |
+--------------------------------------------------------------+
|  已选目录: /app/data/obsidian-notes                           |
|                                [取消]   [选择当前文件夹]     |
+--------------------------------------------------------------+
```

### 5.2 页面适配与生命周期联动
在 `Sources.js` 中：
1. 引入 `FolderPickerModal` 组件；
2. 状态变量 `showFolderPicker = ref(false)`；
3. 点击“选择文件夹”事件：
   - 若处于桌面环境（`isDesktop == true`），优先调用桌面原生对话框；如调用失败或处于纯 Web 环境（`isDesktop == false`），唤起 `FolderPickerModal`；
4. 确认选择回调：
   - 更新 `newSource.path = selectedPath`；
   - 若 `newSource.name` 未填写，自动取末尾文件夹名填入；
   - 给出轻量成功提示。

---

## 6. 测试与质量门禁规划

1. **后端单元与契约测试 (`ReferenceRAG.Host.Tests`)**：
   - 测试 Windows 虚拟根与磁盘列举（或在跨平台 mock 环境下的行为）；
   - 测试正规化路径防目录穿越（`..`、Windows 命名保留字）；
   - 测试异常处理（不存在路径 404、无权访问 403）；
   - 测试 `mkdir` 正常创建与重复创建校验。
2. **前端语法与绑定门禁**：
   - 运行 `node check.mjs` 门禁，确保新组件及 `Sources.js` 的 AST 静态解析、模板绑定以及 `__returned__` 作用域 100% 通过；
   - 执行 `ControllerDependencyInjectionQualityGateTests` 保证 `FileSystemController` 的 DI 激活符合唯一公有构造函数标准。
3. **端到端功能验证**：
   - 验证 Web 模式下点击按钮展开弹窗；
   - 验证上一级、下钻、面包屑导航；
   - 验证“选择当前文件夹”能无缝回填至表单。
