using FolderSync.Infrastructure;

namespace FolderSync.Tests.Helpers;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);

    public void Set(DateTimeOffset time) => _now = time;
}
