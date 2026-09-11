using Xunit;

// Tests in this assembly run one at a time.
//
// They are not unit tests in the sense that lets a runner interleave them freely: a
// good many bind real sockets, and one moves 64 MiB through a real file. Run in
// parallel those compete for ports the operating system has just handed out and for
// disk, and the failures that produces land on whichever test was unlucky rather than
// on the one that caused them. Three CI runs in this project died that way, each in a
// different class, each looking like a bug in code that was fine.
//
// The price is the whole suite taking a few seconds longer. The thing being bought is
// that a red run means something.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
