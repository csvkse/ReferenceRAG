namespace ReferenceRAG.Service.Controllers;

public class IndexSummary
{
    public string CurrentModel { get; set; } = "";
    public int CurrentDimension { get; set; }
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public int SourceCount { get; set; }
    public double AvgQueryTime { get; set; }
    public List<ModelStat> ModelStats { get; set; } = new();
}

public class ModelStat
{
    public string ModelName { get; set; } = "";
    public int Dimension { get; set; }
    public int VectorCount { get; set; }
    public double StorageMB { get; set; }
    public bool IsCurrentModel { get; set; }
}

public class VectorModelIndex
{
    public string ModelName { get; set; } = "";
    public int Dimension { get; set; }
    public int VectorCount { get; set; }
    public long StorageBytes { get; set; }
    public DateTime? LastUpdated { get; set; }
    public bool IsCurrentModel { get; set; }
    public bool DimensionMatch { get; set; }
    public bool ModelExists { get; set; }
}

public class DeleteResult
{
    public string ModelName { get; set; } = "";
    public int DeletedCount { get; set; }
    public string Message { get; set; } = "";
}

public class BulkDeleteResult
{
    public List<DeleteResult> Results { get; set; } = new();
    public int TotalDeleted { get; set; }
}

public class CleanupResult
{
    public int DeletedCount { get; set; }
    public string Message { get; set; } = "";
}

public class RebuildJob
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int Dimension { get; set; }
    public List<string> Sources { get; set; } = new();
    public string Message { get; set; } = "";
}

public class RebuildRequest
{
    public List<string>? Sources { get; set; }
    public bool DeleteExisting { get; set; } = true;
}

public class IndexJobRequest
{
    public List<string>? Sources { get; set; }
    public bool Force { get; set; }
    public bool VectorOnly { get; set; }
}

public class IndexJobResponse
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public bool Force { get; set; }
    public string Message { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int ProgressPercent { get; set; }
    public string? CurrentFile { get; set; }
    public int Errors { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Duration { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AllJobsResponse
{
    public List<IndexJobResponse> ActiveJobs { get; set; } = new();
    public List<IndexJobResponse> CompletedJobs { get; set; } = new();
}
