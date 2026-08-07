using Xunit;

// ConfigurationService assigns a process-wide static Instance in its
// constructor, so two test classes constructing one concurrently would race.
// The auto-save suite is small and IO-bound on unique temp directories; running
// it serially costs nothing and removes that shared-state hazard entirely.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
