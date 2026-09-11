using Xunit;

// Every test in this project drives one shared, machine-global resource: the single
// Galbox.App.exe installation and its single %LocalAppData%\Galbox state directory
// (galbox.db, logs\, ScrapingCache\). xunit would otherwise run the test classes in parallel,
// two launcher tests would race for the same window/process, and the resulting flakiness would
// be indistinguishable from a real startup defect. Disable parallelisation here so a failure
// always means the application failed, never that two tests collided.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
