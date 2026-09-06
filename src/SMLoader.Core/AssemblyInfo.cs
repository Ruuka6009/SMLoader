using System.Runtime.CompilerServices;

// Banner, KeyNames and ProcessMemory.ParsePattern are internal because nothing
// outside the loader should call them - but they are pure functions with exactly
// the kind of boundary behaviour worth pinning down, so the test project sees
// them rather than being made to go the long way round.
[assembly: InternalsVisibleTo("SMLoader.Tests")]
