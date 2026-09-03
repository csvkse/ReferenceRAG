# ASP.NET Core 配置系统详解

## 配置加载机制

### 配置提供程序优先级

ASP.NET Core 默认配置加载顺序（优先级从低到高）：

```csharp
// WebApplication.CreateBuilder() 内部自动配置
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)           // 优先级 1
    .AddJsonFile($"appsettings.{Environment}.json", optional: true)  // 优先级 2
    .AddEnvironmentVariables()                                  // 优先级 3
    .AddCommandLine(args);                                      // 优先级 4（最高）
```

**优先级规则**：
- 后添加的提供程序优先级更高
- 相同键名，后加载的值会覆盖先加载的值
- 环境变量 > appsettings.{Environment}.json > appsettings.json

## IConfiguration.GetValue 方法

### 方法定义

**命名空间**：`Microsoft.Extensions.Configuration`
**类型**：扩展方法（框架内置）

```csharp
public static T GetValue<T>(this IConfiguration configuration, string key, T defaultValue)
```

**源码实现**（简化版）：
```csharp
public static T GetValue<T>(this IConfiguration configuration, string key, T defaultValue)
{
    // 1. 通过索引器获取值（自动遍历所有配置提供程序）
    var value = configuration[key];

    // 2. 如果不存在，返回默认值
    if (value == null)
        return defaultValue;

    // 3. 类型转换
    return (T)Convert.ChangeType(value, typeof(T));
}
```

### 配置查找过程

```csharp
builder.Configuration.GetValue<bool>("SwaggerEnabled", false)
```

**内部执行流程**：
1. 调用 `configuration["SwaggerEnabled"]` 索引器
2. 索引器按优先级查找：
   - 检查命令行参数
   - 检查环境变量 `SwaggerEnabled`
   - 检查 `appsettings.Production.json`
   - 检查 `appsettings.json`
3. 返回第一个找到的值
4. 类型转换为 `bool`

## 配置层级结构

### JSON 配置文件层级

```json
{
  "SwaggerEnabled": true,              // ✅ 根级别（第一层）
  "AllowedHosts": "*",                 // ✅ 根级别
  "ReferenceRAG": {                    // ✅ 根级别
    "service": {                       // ❌ 第二层
      "enableSwagger": true,           // ❌ 第三层
      "port": 7897
    }
  }
}
```

### 配置读取方式

**读取根级别配置**：
```csharp
// 方式 1: 使用 GetValue
var enabled = builder.Configuration.GetValue<bool>("SwaggerEnabled", false);

// 方式 2: 使用索引器
var enabled = bool.Parse(builder.Configuration["SwaggerEnabled"] ?? "false");

// 方式 3: 绑定到对象
var settings = new Settings();
builder.Configuration.Bind(settings);
```

**读取嵌套配置**：
```csharp
// 使用冒号分隔符
var port = builder.Configuration.GetValue<int>("ReferenceRAG:service:port", 5000);

// 使用 GetSection
var section = builder.Configuration.GetSection("ReferenceRAG:service");
var port = section.GetValue<int>("port", 5000);

// 绑定到强类型对象
var config = builder.Configuration.GetSection("ReferenceRAG").Get<ObsidianRagConfig>();
```

## 环境变量配置

### 环境变量命名规则

**平级配置**：
```bash
# 对应 JSON: "SwaggerEnabled": true
export SwaggerEnabled=true
```

**嵌套配置**（使用双下划线 `__`）：
```bash
# 对应 JSON: ReferenceRAG.service.enableSwagger
export ReferenceRAG__service__enableSwagger=true

# 对应 JSON: ReferenceRAG.service.port
export ReferenceRAG__service__port=7897
```

**Linux/macOS**：
```bash
export ReferenceRAG__service__enableSwagger=true
```

**Windows (PowerShell)**：
```powershell
$env:ReferenceRAG__service__enableSwagger="true"
```

**Windows (CMD)**：
```cmd
set ReferenceRAG__service__enableSwagger=true
```

### Docker 环境变量配置

**docker-compose.yml**：
```yaml
services:
  reference-rag:
    environment:
      - SwaggerEnabled=true
      - ReferenceRAG__service__port=5000
      - ReferenceRAG__service__host=0.0.0.0
```

**Dockerfile**：
```dockerfile
ENV SwaggerEnabled=true
ENV ReferenceRAG__service__port=5000
```

## 实际案例：Swagger 配置问题

### 问题场景

**配置文件** (`appsettings.json`)：
```json
{
  "ReferenceRAG": {
    "service": {
      "enableSwagger": true,  // ❌ 不会被读取
      "port": 7897
    }
  }
}
```

**代码**：
```csharp
var swaggerEnabled = builder.Configuration.GetValue<bool>("SwaggerEnabled", false);
// 读取根级别 "SwaggerEnabled"，不读取嵌套配置
```

**结果**：Swagger 未启用（返回默认值 `false`）

### 解决方案

**方案 1：修改代码读取嵌套配置**（推荐）
```csharp
var swaggerEnabled = builder.Configuration
    .GetValue<bool>("ReferenceRAG:service:enableSwagger", false);
```

**方案 2：在配置文件根级别添加**
```json
{
  "SwaggerEnabled": true,  // ✅ 根级别
  "ReferenceRAG": {
    "service": {
      "enableSwagger": true
    }
  }
}
```

**方案 3：使用环境变量**
```bash
# 启动时设置
SwaggerEnabled=true dotnet run

# 或在 docker-compose.yml
environment:
  - SwaggerEnabled=true
```

**方案 4：使用环境变量覆盖嵌套配置**
```bash
export ReferenceRAG__service__enableSwagger=true
```

## 配置最佳实践

### 1. 配置结构设计

**推荐结构**：
```json
{
  // 应用级配置（根级别）
  "SwaggerEnabled": false,
  "ApiKey": "",
  "AllowedHosts": "*",

  // 模块配置（嵌套）
  "ReferenceRAG": {
    "dataPath": "/app/data",
    "modelsRootPath": "/app/models",
    "service": {
      "port": 5000,
      "host": "0.0.0.0"
    }
  }
}
```

**理由**：
- 应用级配置放在根级别，便于环境变量覆盖
- 模块配置使用嵌套结构，保持组织性

### 2. 环境特定配置

**开发环境** (`appsettings.Development.json`)：
```json
{
  "SwaggerEnabled": true,
  "ReferenceRAG": {
    "service": {
      "port": 7897,
      "host": "localhost"
    }
  }
}
```

**生产环境** (`appsettings.Production.json`)：
```json
{
  "SwaggerEnabled": false,
  "ReferenceRAG": {
    "service": {
      "port": 5000,
      "host": "0.0.0.0"
    }
  }
}
```

### 3. 敏感配置管理

**不要在配置文件中存储敏感信息**：
```json
{
  "ApiKey": "",  // ❌ 不要硬编码
  "ConnectionString": ""  // ❌ 不要硬编码
}
```

**使用环境变量**：
```bash
export ApiKey="your-secret-key"
export ConnectionString="Server=...;Password=..."
```

**或使用用户机密**（开发环境）：
```bash
dotnet user-secrets init
dotnet user-secrets set "ApiKey" "your-secret-key"
```

### 4. 强类型配置

**定义配置类**：
```csharp
public class ReferenceRagConfig
{
    public string DataPath { get; set; }
    public ServiceConfig Service { get; set; }
}

public class ServiceConfig
{
    public int Port { get; set; }
    public string Host { get; set; }
}
```

**绑定配置**：
```csharp
// 方式 1: IOptions
builder.Services.Configure<ReferenceRagConfig>(
    builder.Configuration.GetSection("ReferenceRAG"));

// 方式 2: 直接绑定
var config = builder.Configuration
    .GetSection("ReferenceRAG")
    .Get<ReferenceRagConfig>();
```

**使用配置**：
```csharp
public class MyService
{
    private readonly ReferenceRagConfig _config;

    public MyService(IOptions<ReferenceRagConfig> options)
    {
        _config = options.Value;
    }
}
```

## 配置调试技巧

### 1. 打印所有配置

```csharp
var configuration = builder.Configuration;

// 打印所有配置源
foreach (var source in configuration.Sources)
{
    Console.WriteLine($"配置源: {source.GetType().Name}");
}

// 打印所有配置键值对
static void PrintConfiguration(IConfiguration config, string prefix = "")
{
    foreach (var child in config.GetChildren())
    {
        Console.WriteLine($"{prefix}{child.Key} = {child.Value}");
        PrintConfiguration(child, prefix + "  ");
    }
}

PrintConfiguration(configuration);
```

### 2. 检查特定配置

```csharp
// 检查配置是否存在
var section = builder.Configuration.GetSection("SwaggerEnabled");
Console.WriteLine($"Exists: {section.Exists()}");
Console.WriteLine($"Value: {section.Value}");

// 检查嵌套配置
var nested = builder.Configuration.GetSection("ReferenceRAG:service:enableSwagger");
Console.WriteLine($"Nested Value: {nested.Value}");
```

### 3. 配置重载

**启用配置文件热重载**：
```csharp
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
```

**监听配置变更**：
```csharp
ChangeToken.OnChange(
    () => builder.Configuration.GetReloadToken(),
    () => {
        var newValue = builder.Configuration["SwaggerEnabled"];
        Console.WriteLine($"配置已更新: {newValue}");
    });
```

## 常见问题

### Q1: 为什么环境变量没有生效？

**检查清单**：
1. 环境变量名称是否正确（区分大小写）
2. 嵌套配置是否使用 `__` 分隔符
3. 是否在正确的环境中设置（Development/Production）
4. 是否在 `WebApplication.CreateBuilder()` 之后读取

### Q2: 配置文件修改后不生效？

**原因**：
- 配置在应用启动时加载，运行时不会自动重载
- 需要重启应用或启用 `reloadOnChange: true`

### Q3: Docker 环境变量不生效？

**检查**：
```bash
# 进入容器查看环境变量
docker exec <container> env | grep SwaggerEnabled

# 查看配置文件
docker exec <container> cat /app/appsettings.json
```

### Q4: 如何查看实际使用的配置值？

**调试代码**：
```csharp
var logger = builder.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>();
logger.LogInformation($"SwaggerEnabled: {builder.Configuration.GetValue<bool>("SwaggerEnabled")}");
logger.LogInformation($"Port: {builder.Configuration.GetValue<int>("ReferenceRAG:service:port")}");
```

## 参考资料

- [ASP.NET Core 配置](https://learn.microsoft.com/aspnet/core/fundamentals/configuration)
- [环境变量配置提供程序](https://learn.microsoft.com/aspnet/core/fundamentals/configuration#environment-variables)
- [配置绑定](https://learn.microsoft.com/aspnet/core/fundamentals/configuration#bind-to-a-class)

## 更新日志

- **2026-04-19**: 初始版本，记录 ASP.NET Core 配置系统详解
