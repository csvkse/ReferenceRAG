# 数据库路径与文件数问题分析

## 问题描述

每次更新程序后，文件数会变回原来的值。

## 问题根源

### 数据库存储位置

文件索引数据存储在 SQLite 数据库中，路径由 `appsettings.json` 中的 `dataPath` 配置决定：

```json
"ReferenceRAG": {
    "dataPath": "E:/LinuxWork/Obsidian/resource/data"
}
```

完整数据库路径：`{DataPath}/vectors.db`

### 默认值配置

**ObsidianRagConfig.cs:11**
```csharp
public string DataPath { get; set; } = "data";  // 默认是相对路径
```

### 数据库初始化

**Program.cs:149-150**
```csharp
var dataPath = cfg.DataPath ?? "data";
var dbPath = Path.Combine(dataPath, "vectors.db");
return new SqliteVectorStore(dbPath);
```

## 为什么会"变回原来的值"

| 场景 | 结果 |
|------|------|
| 部署时覆盖了 `appsettings.json` | `DataPath` 变回默认值 `"data"` |
| 从不同目录启动程序 | 相对路径 `"data"` 指向不同位置 |
| 新位置的 `data/` 是空目录 | 读到空的 `vectors.db`，文件数为 0 |

## 解决方案

### 方案 1：保留配置文件

更新程序时不要覆盖 `appsettings.json`，确保 `dataPath` 保持原值。

### 方案 2：使用绝对路径

在 `appsettings.json` 中配置绝对路径：

```json
"dataPath": "E:/LinuxWork/Obsidian/resource/data"
```

### 方案 3：确保工作目录一致

启动程序时保持相同的工作目录，避免相对路径解析到不同位置。

### 方案 4：使用环境变量

```bash
# Windows
set OBSIDIAN_RAG_DATA_PATH=E:/LinuxWork/Obsidian/resource/data

# Linux/macOS
export OBSIDIAN_RAG_DATA_PATH=E:/LinuxWork/Obsidian/resource/data
```

## 验证方法

1. 检查当前数据库路径：
   ```bash
   # 查看配置文件
   cat appsettings.json | grep dataPath
   ```

2. 确认数据库文件存在：
   ```bash
   ls -la {DataPath}/vectors.db
   ```

3. 检查实际加载的配置：
   ```bash
   # 启动时观察日志
   # 应该显示 "已从 appsettings.json 加载配置"
   ```

## 相关文件

| 文件 | 作用 |
|------|------|
| `appsettings.json` | 配置文件，包含 dataPath |
| `ObsidianRagConfig.cs` | 配置模型定义 |
| `Program.cs` | 服务启动，初始化数据库 |
| `SqliteVectorStore.cs` | SQLite 数据库实现 |
| `DashboardController.cs` | 获取文件数的 API |

## 更新日志

- **2026-04-20**: 初始版本，记录文件数重置问题分析
