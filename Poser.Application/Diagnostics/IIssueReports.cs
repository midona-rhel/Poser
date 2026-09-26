namespace Poser.Application.Diagnostics;

public interface IIssueReports
{
    bool Pending { get; }
    void Save(bool includeScene, Action<string> done, Action<string> failed);
}
