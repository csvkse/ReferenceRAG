namespace ReferenceRAG.Core.Models;

public class GraphNode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "document";
    public List<string> ChunkIds { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class GraphEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Type { get; set; } = "wikilink";
    public int LineNumber { get; set; }
}

public class GraphTraversalResult
{
    public string RootId { get; set; } = "";
    public int Depth { get; set; }
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
}

public class GraphStats
{
    public int NodeCount { get; set; }
    public int DocCount { get; set; }
    public int TagCount { get; set; }
    public int HeadingCount { get; set; }
    public int ExternalCount { get; set; }
    public int EdgeCount { get; set; }
}
