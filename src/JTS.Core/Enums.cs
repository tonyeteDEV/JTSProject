namespace JTS.Core;

public enum WorkType
{
    DeepWork,
    Admin,
    Meeting,
    Learning,
    Communication,
    Planning,
    Other
}

public enum TaskPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum TaskItemStatus
{
    Backlog,
    Todo,
    Assigned,
    InProgress,
    Blocked,
    Testing,
    Tested,
    UAT,
    Done,
    Cancelled
}

public enum PomodoroSessionType
{
    Work,
    ShortBreak,
    LongBreak
}

public enum ProjectRelationType
{
    RelatedTo,
    DependsOn,
    Blocks,
    Supersedes
}
