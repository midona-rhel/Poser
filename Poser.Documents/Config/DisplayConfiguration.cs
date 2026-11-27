namespace Poser.Documents.Config;

/// <summary>
/// Anonymous mode and whether NSFW bones are shown: what the Display page's
/// Reset puts back.
/// </summary>
public class DisplayConfiguration
{
    public bool ShowNsfwBones { get; set; } = true;
    public bool AnonymousMode { get; set; } = false;
}
