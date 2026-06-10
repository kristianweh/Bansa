using Xunit;

// Several targets read/mutate the shared App.Settings static (Format unit, HeatColors
// thresholds), so test classes must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
