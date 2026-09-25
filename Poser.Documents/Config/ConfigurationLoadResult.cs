namespace Poser.Config;

public sealed record ConfigurationLoadResult(PoserConfiguration Configuration, string Failure = "");
