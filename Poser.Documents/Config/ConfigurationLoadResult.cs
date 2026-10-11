namespace Poser.Documents.Config;

public sealed record ConfigurationLoadResult(PoserConfiguration Configuration, string Failure = "");
