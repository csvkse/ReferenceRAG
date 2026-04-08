using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Storage;
using ObsidianRAG.Service.Hubs;
using ObsidianRAG.Service.Services;
using ObsidianRAG.Service.Controllers;
using Microsoft.OpenApi;
using System.Text.Json.Serialization;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding= System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Obsidian RAG API",
        Version = "v1",
        Description = "Obsidian 笔记库向量检索 API"
    });
});

// 注册配置管理
builder.Services.AddSingleton<ConfigManager>();

// 注册核心服务
builder.Services.AddSingleton<ITokenizer, SimpleTokenizer>();
builder.Services.AddSingleton<ITextEnhancer, TextEnhancer>();
builder.Services.AddSingleton<IMarkdownChunker, MarkdownChunker>();
builder.Services.AddSingleton<IVectorStore>(sp => 
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();
    var dataPath = cfg.DataPath ?? "data";
    return new JsonVectorStore(dataPath);
});
builder.Services.AddSingleton<IEmbeddingService>(sp =>
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();
    return new EmbeddingService(new EmbeddingOptions
    {
        ModelPath = cfg.Embedding.ModelPath,
        ModelName = cfg.Embedding.ModelName,
        MaxSequenceLength = cfg.Embedding.MaxSequenceLength,
        BatchSize = cfg.Embedding.BatchSize,
        UseCuda = cfg.Embedding.UseCuda,
        CudaDeviceId = cfg.Embedding.CudaDeviceId,
        CudaLibraryPath = cfg.Embedding.CudaLibraryPath
    });
});

// 注册业务服务
builder.Services.AddSingleton<ContentHashDetector>();
builder.Services.AddSingleton<QueryOptimizer>();
builder.Services.AddSingleton<VectorAggregator>();
builder.Services.AddSingleton<ContextBuilder>();
builder.Services.AddSingleton<ObsidianLinkGenerator>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<AlertService>();
// FileChangeDetector 需要配置路径，使用工厂方法延迟创建
builder.Services.AddSingleton<IFileChangeDetector>(sp => 
{
    var configManager = sp.GetRequiredService<ConfigManager>();
    var config = configManager.Load();
    var firstSource = config.Sources.FirstOrDefault();
    return new FileChangeDetector(firstSource?.Path ?? Directory.GetCurrentDirectory());
});
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<HierarchicalSearchService>();

// 注册索引服务（后台服务）
builder.Services.AddSingleton<IndexService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexService>());

// 注册测试记录存储
builder.Services.AddSingleton<TestRecordStore>();

// 注册 SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// 配置 CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Obsidian RAG API v1");
    });
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

// 映射 SignalR Hub
app.MapHub<IndexHub>("/hubs/index");

// 确保数据目录存在
var configManager = app.Services.GetRequiredService<ConfigManager>();
var config = configManager.Load();
var dataPath = config.DataPath ?? "data";
if (!Directory.Exists(dataPath))
{
    Directory.CreateDirectory(dataPath);
}

// 确保模型目录存在
var modelDir = Path.GetDirectoryName(config.Embedding.ModelPath);
if (!string.IsNullOrEmpty(modelDir) && !Directory.Exists(modelDir))
{
    Directory.CreateDirectory(modelDir);
}

app.Run();
