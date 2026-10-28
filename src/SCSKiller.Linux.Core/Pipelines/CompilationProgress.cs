namespace SCSKiller.Linux.Core.Pipelines;

public record CompilationProgress(
    string GameId,
    string GameName,
    int ProcessedShaders,
    int TotalShaders,
    double Percentage,
    string CurrentAction,
    bool IsComplete,
    bool Failed,
    string? ErrorMessage
);
