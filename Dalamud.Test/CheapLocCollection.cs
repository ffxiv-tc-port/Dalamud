using Xunit;

namespace Dalamud.Test
{
    // Tests that set up or read CheapLoc's tables: they are plain dictionaries, so these must not run in parallel.
    [CollectionDefinition(Name)]
    public class CheapLocCollection
    {
        public const string Name = "CheapLoc tables";
    }
}
