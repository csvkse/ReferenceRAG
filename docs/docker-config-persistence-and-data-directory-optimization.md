# 统一数据目录配置文件架构设计 (data/appsettings.json)

> 文档编号: `ARCH-2026-DOCKER-CFG-01`  
> 日期: `2026-10-22`  
> 状态: `已实施 (Implemented)`  
> 关联组件: `ConfigPathResolver`, `ConfigBootstrap`, `ConfigManager`, `WebHost/Program.cs`, `DesktopHost/Program.cs`

---

## 一、设计目标与原则 (Design Objectives)

1. **绝对极简统一**：
   抛弃多套条件判断与回退逻辑，全平台（Docker 容器、桌面端 DesktopHost、Web 服务端）**默认统一直接使用 `程序所在目录/data/appsettings.json` 作为配置文件物理路径**。
2. **零文件挂载陷阱 (Zero Single-File Mount Trap)**：
   用户只需挂载常规目录（`-v .../data:/app/data`），严禁要求挂载单文件。
3. **首次启动自愈释放 (Zero-Config First Run)**：
   程序首次冷启动且 `data` 目录为空时，`ConfigBootstrap` 自动从模板（镜像内置配置、伴生模板或代码默认值）释放完整的 `appsettings.json` 到 `data/` 目录中。
4. **升级与重启持久化无损 (Safe Upgrades & Persistence)**：
   Web 控制台修改的所有配置实时写入 `data/appsettings.json`。在 Docker 容器重建、拉取新镜像或桌面端覆盖升级时，挂载/存储在 `data/` 中的配置文件永久保留，且启动时自动平滑执行版本结构迁移。
5. **宿主机直接离线修改 (Offline Host Editing)**：
   用户可随时通过文本编辑器直接在宿主机打开 `data/appsettings.json` 调整参数，重启即可生效。

---

## 二、核心实现架构 (Implementation Architecture)

### 1. 极简路径解析器 (`ConfigPathResolver`)
```csharp
public static class ConfigPathResolver
{
    public const string DefaultConfigFileName = "appsettings.json";
    public const string ConfigPathEnvVar = "REFERENCERAG_CONFIG_PATH";

    public static string ResolveConfigPath(string? baseDirectory = null)
    {
        // 1. 若显式指定环境变量，则以其绝对路径为准
        var explicitConfigPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            return Path.GetFullPath(explicitConfigPath);
        }

        var resolvedBaseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? (Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? Directory.GetCurrentDirectory())
            : Path.GetFullPath(baseDirectory);

        // 2. 默认直接定位到：程序所在目录/data/appsettings.json
        var dataDir = Environment.GetEnvironmentVariable("REFERENCERAG_DATA_PATH")
                      ?? Environment.GetEnvironmentVariable("DataPath")
                      ?? Environment.GetEnvironmentVariable("OBSIDIAN_RAG_DATA_PATH")
                      ?? Path.Combine(resolvedBaseDir, "data");

        var fullDataDir = Path.IsPathRooted(dataDir)
            ? Path.GetFullPath(dataDir)
            : Path.GetFullPath(Path.Combine(resolvedBaseDir, dataDir));

        return Path.Combine(fullDataDir, DefaultConfigFileName);
    }
}
```

### 2. 宿主程序直接接入 (`Program.cs`)
在 WebHost 与 DesktopHost 的入口中：
```csharp
ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigBootstrap.EnsureInitialized();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ... });

var activeConfigPath = ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigBootstrap.ActiveConfigPath;
builder.Configuration.AddJsonFile(activeConfigPath, optional: true, reloadOnChange: true);
```
保证 ASP.NET Core 的 `IConfiguration` 统一加载 `data/appsettings.json`。

### 3. 配置管理器读写统一 (`ConfigManager.cs`)
- `GetAppSettingsPath()` 与 `GetConfigPath()` 统一调用 `ConfigPathResolver.ResolveConfigPath()`。
- `Save()` 在写入前自动创建目标父级目录，杜绝冷启动时出现 `DirectoryNotFoundException`。

---

## 三、验证结果 (Verification Matrix)

| 测试套件 | 测试项目 | 状态 |
| :--- | :--- | :--- |
| `ConfigPathResolverTests` | 默认解析到 `data/appsettings.json`、自定义 DataPath、显式环境变量覆盖、冷启动自愈释放 | **全部通过** |
| `ConfigManagerTests` | 内存读写、配置序列化、源路径去重管理（针对 `data/appsettings.json`） | **全部通过** |
| `MafChatControllerConfigTests` | 动态加载与独立配置管理 | **全部通过** |
| `ConfigBootstrapStateMachineTests` | 容灾自愈、备份与版本迁移 | **全部通过** |
| `verify-gate.ps1` | 全套前端 AST 静态绑定校验与 116 项核心契约回归测试 | **全部通过** |

---

## 四、Docker 推荐运行命令

```bash
docker run -d \
  --name reference-rag-intel \
  --device=/dev/dri \
  -p 5123:5000 \
  -v /mnt/disk1/domains/Docker/dc/referencerag/data:/app/data \
  -v /mnt/disk1/domains/Docker/dc/referencerag/models:/app/models \
  -v /mnt/disk1/domains/Docker/dc/referencerag/logs:/app/logs \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:intel
```
首次启动容器会自动在挂载的 `data/` 目录中生成 `appsettings.json`；重启或拉取新镜像升级，配置永不丢失。
