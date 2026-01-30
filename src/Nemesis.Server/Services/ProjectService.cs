using Nemesis.Shared.DTOs;

namespace Nemesis.Server.Services;

public class ProjectService
{
    private ProjectInfo? _currentProject;
    private readonly object _lock = new();

    public event Action<ProjectInfo?>? ProjectChanged;

    public ProjectInfo? CurrentProject
    {
        get
        {
            lock (_lock) return _currentProject;
        }
        set
        {
            lock (_lock) _currentProject = value;
            ProjectChanged?.Invoke(value);
        }
    }

    public bool IsProjectLoaded => CurrentProject != null;
    public bool IsProjectIndexed => CurrentProject?.IsIndexed == true;
}
