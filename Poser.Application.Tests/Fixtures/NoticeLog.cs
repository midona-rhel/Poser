using Poser.Application.Presentation;

namespace Poser.Application.Tests.Fixtures;

/// <summary>Every notice posted, in order, whatever its kind.</summary>
internal sealed class NoticeLog : List<string>, IUserNotices
{
    public void Note(string message) => Add(message);
    public void Refused(string message) => Add(message);
    public void Failed(string message) => Add(message);
}
