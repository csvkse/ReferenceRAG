using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;

namespace LexicalRecoveryCrashWorker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? action = null;
        string? dir = null;
        string fileId = "test-file";
        string generation = "v1";
        string token = "default_token";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--action" && i + 1 < args.Length) action = args[++i];
            else if (args[i] == "--dir" && i + 1 < args.Length) dir = args[++i];
            else if (args[i] == "--fileId" && i + 1 < args.Length) fileId = args[++i];
            else if (args[i] == "--gen" && i + 1 < args.Length) generation = args[++i];
            else if (args[i] == "--token" && i + 1 < args.Length) token = args[++i];
        }

        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(dir))
        {
            Console.Error.WriteLine("Usage: --action <action> --dir <dir> [--fileId <id>] [--gen <gen>] [--token <tok>]");
            return 1;
        }

        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        await using var coordinator = new LexicalIndexCoordinator(docs, index);

        var file = new FileRecord
        {
            Id = fileId,
            Path = Path.Combine(dir, $"{fileId}.md"),
            FileName = $"{fileId}.md",
            Source = "source-a",
            Title = fileId
        };
        var chunk = new ChunkRecord
        {
            Id = $"{fileId}-c1",
            FileId = fileId,
            Source = "source-a",
            Content = $"# {fileId}\n\n{token}\n\nWorker content."
        };

        switch (action)
        {
            case "stage-docs":
                await coordinator.StageDocumentsAsync(file, generation, new[] { chunk });
                SignalReady();
                await Task.Delay(Timeout.Infinite);
                break;

            case "stage-fts":
                await coordinator.StageDocumentsAsync(file, generation, new[] { chunk });
                await coordinator.StageIndexAsync(fileId, generation);
                SignalReady();
                await Task.Delay(Timeout.Infinite);
                break;

            case "publish":
                await coordinator.StageDocumentsAsync(file, generation, new[] { chunk });
                await coordinator.StageIndexAsync(fileId, generation);
                await coordinator.PublishAsync(fileId, generation);
                SignalReady();
                await Task.Delay(Timeout.Infinite);
                break;

            case "delete-file":
                await coordinator.DeleteFileAsync(fileId);
                SignalReady();
                await Task.Delay(Timeout.Infinite);
                break;

            case "begin-recovery":
                var recovery = await coordinator.BeginVectorRecoveryAsync(new[] { "*" });
                SignalReady();
                await Task.Delay(Timeout.Infinite);
                break;

            default:
                Console.Error.WriteLine($"Unknown action: {action}");
                return 2;
        }

        return 0;
    }

    private static void SignalReady()
    {
        Console.WriteLine($"SIGNAL:READY:PID:{Environment.ProcessId}");
        Console.Out.Flush();
    }
}
