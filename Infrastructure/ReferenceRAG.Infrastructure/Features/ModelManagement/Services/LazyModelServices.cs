using System.Text.Json;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Core.Services;

/// <summary>Defers the heavyweight ONNX/CUDA session until an operation actually needs inference.</summary>
internal sealed class LazyEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly IGpuMemoryManager? _memoryManager;
    private readonly Func<EmbeddingService> _factory;
    private readonly object _gate = new();
    private EmbeddingService? _inner;

    public LazyEmbeddingService(EmbeddingOptions options, IGpuMemoryManager? memoryManager, Func<EmbeddingService>? factory = null)
    {
        _options = options;
        _memoryManager = memoryManager;
        _factory = factory ?? (() => new EmbeddingService(_options, _memoryManager));
        Dimension = ReadConfiguredDimension(options.ModelPath);
        if(options.UseCuda && memoryManager is not null)
            memoryManager.Register("EmbeddingService",()=>null,options.CudaDeviceId,()=>{UnloadModel();return Task.CompletedTask;});
    }

    internal bool IsModelLoaded => _inner is not null;
    public string ModelName => _inner?.ModelName ?? _options.ModelName;
    public int Dimension { get; private set; }
    public bool IsSimulationMode => _inner?.IsSimulationMode ?? false;
    public bool SupportsAsymmetricEncoding => _inner?.SupportsAsymmetricEncoding ?? _options.AsymmetricEncoding is not null;

    private EmbeddingService GetOrCreate()
    {
        if (_inner is not null) return _inner;
        lock (_gate)
        {
            _inner ??= _factory();
            Dimension = _inner.Dimension;
            return _inner;
        }
    }

    private static int ReadConfiguredDimension(string modelPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(directory)) return 0;
            foreach (var path in new[] { Path.Combine(directory,"1_Pooling","config.json"), Path.Combine(directory,"config.json") })
            {
                if (!File.Exists(path)) continue;
                using var json=JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("word_embedding_dimension",out var pooling)) return pooling.GetInt32();
                if (json.RootElement.TryGetProperty("hidden_size",out var hidden)) return hidden.GetInt32();
            }
        }
        catch { /* Dimension is finalized when the model is first used. */ }
        return 0;
    }

    public async Task<bool> ReloadModelAsync(string modelPath,string modelName,int? maxSequenceLength=null)
    { _options.ModelPath=modelPath;_options.ModelName=modelName;if(maxSequenceLength.HasValue)_options.MaxSequenceLength=maxSequenceLength.Value;var result=await GetOrCreate().ReloadModelAsync(modelPath,modelName,maxSequenceLength);Dimension=_inner!.Dimension;return result; }
    public void UnloadModel(){lock(_gate){_inner?.UnloadModel();_inner?.Dispose();_inner=null;}}
    private async Task<T> InferenceAsync<T>(Func<EmbeddingService,Task<T>> action)
    { _memoryManager?.EnterInference("EmbeddingService");try{return await action(GetOrCreate());}finally{_memoryManager?.ExitInference("EmbeddingService");} }
    public Task<float[]> EncodeAsync(string text,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.EncodeAsync(text,cancellationToken));
    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.EncodeBatchAsync(texts,cancellationToken));
    public Task<float[]> EncodeAsync(string text,EmbeddingMode mode,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.EncodeAsync(text,mode,cancellationToken));
    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts,EmbeddingMode mode,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.EncodeBatchAsync(texts,mode,cancellationToken));
    public float[] Normalize(float[] vector)
    {
        var sum=0f;for(var i=0;i<vector.Length;i++)sum+=vector[i]*vector[i];
        var norm=MathF.Sqrt(sum);if(norm<1e-10f)return vector;
        for(var i=0;i<vector.Length;i++)vector[i]/=norm;return vector;
    }
    public float Similarity(float[] a,float[] b)
    { var dot=0f;for(var i=0;i<a.Length;i++)dot+=a[i]*b[i];return dot; }
    public void Dispose(){UnloadModel();_memoryManager?.Unregister("EmbeddingService");}
}

internal sealed class LazyRerankService : IRerankService, IDisposable
{
    private readonly Func<OnnxRerankService> _factory;
    private readonly IGpuMemoryManager? _memoryManager;
    private readonly bool _useCuda;
    private readonly object _gate=new();
    private OnnxRerankService? _inner;
    private string _modelName;
    public LazyRerankService(string modelName,Func<OnnxRerankService> factory,IGpuMemoryManager? memoryManager=null,bool useCuda=false,int deviceId=0)
    {_modelName=modelName;_factory=factory;_memoryManager=memoryManager;_useCuda=useCuda;if(useCuda&&memoryManager is not null)memoryManager.Register("OnnxRerankService",()=>null,deviceId,()=>{UnloadModel();return Task.CompletedTask;});}
    internal bool IsModelLoaded=>_inner is not null;
    public string ModelName=>_inner?.ModelName??_modelName;
    public bool IsLoaded=>_inner?.IsLoaded??false;
    private OnnxRerankService GetOrCreate(){if(_inner is not null)return _inner;lock(_gate)return _inner??=_factory();}
    private async Task<T> InferenceAsync<T>(Func<OnnxRerankService,Task<T>> action)
    {_memoryManager?.EnterInference("OnnxRerankService");try{return await action(GetOrCreate());}finally{_memoryManager?.ExitInference("OnnxRerankService");}}
    public Task<double> RerankAsync(string query,string document,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.RerankAsync(query,document,cancellationToken));
    public Task<RerankResult> RerankBatchAsync(string query,IEnumerable<string> documents,CancellationToken cancellationToken=default)=>InferenceAsync(x=>x.RerankBatchAsync(query,documents,cancellationToken));
    public async Task<bool> ReloadModelAsync(string path,string name){_modelName=name;return await GetOrCreate().ReloadModelAsync(path,name);}
    public void UnloadModel(){lock(_gate){_inner?.UnloadModel();_inner?.Dispose();_inner=null;}}
    public void Dispose(){UnloadModel();if(_useCuda)_memoryManager?.Unregister("OnnxRerankService");}
}
