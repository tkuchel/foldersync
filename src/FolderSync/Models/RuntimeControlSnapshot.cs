namespace FolderSync.Models;

public sealed class RuntimeControlSnapshot
{
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
    public List<ProfileRuntimeControlSnapshot> Profiles { get; set; } = [];

    public ProfileRuntimeControlSnapshot? GetProfile(string profileName)
    {
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    public ProfileRuntimeControlSnapshot? GetEffectivePause(string profileName)
    {
        if (IsPaused)
        {
            return new ProfileRuntimeControlSnapshot
            {
                Name = profileName,
                IsPaused = true,
                Reason = Reason,
                ChangedAtUtc = ChangedAtUtc
            };
        }

        var profile = GetProfile(profileName);
        return profile?.IsPaused is true ? profile : null;
    }
}

public sealed class ProfileRuntimeControlSnapshot
{
    public required string Name { get; set; }
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
}
