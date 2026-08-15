using Xunit;

// Tests run one at a time, on purpose.
//
// Some of what this suite covers is PROCESS-WIDE state, not per-object state: the UI language
// (Loc.Use switches the catalog every string in the app resolves against), environment variables
// (AppPathsTests), and the working directory. A test that switches the launcher to German is
// therefore visible to every test running beside it, and the ones that assert English text fail for
// a reason that has nothing to do with the code they cover - intermittently, on a machine under
// load, in whichever test happened to overlap.
//
// A named [Collection] only serialises tests INSIDE it, and the readers of Loc are spread across the
// whole suite, so that would not close it. The cost of this is wall-clock; the cost of the
// alternative is a suite that goes red for reasons nobody can reproduce, which is worse than slow.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
