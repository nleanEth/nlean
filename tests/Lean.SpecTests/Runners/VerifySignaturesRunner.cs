using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class VerifySignaturesRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        // leanSpec's verify_signatures fixtures are produced with the TEST
        // signature scheme (a short ~425-byte XMSS variant tuned for fast
        // spec filling). nlean's Rust FFI (Lean.Crypto) only supports the
        // PROD scheme, so verifying a test-scheme signature with prod
        // parameters fails unconditionally — there's no way to replay
        // these fixtures without either re-generating them with --scheme=prod
        // (ream's approach, requires a curated fixture+keys pair) or adding
        // test-scheme support to the FFI.
        Assert.Inconclusive(
            "verify_signatures fixtures use leanSpec's TEST signature scheme, " +
            "which nlean's Lean.Crypto FFI doesn't implement (it only binds " +
            "the PROD scheme). Re-generate fixtures with `uv run fill --scheme=prod` " +
            "alongside matching keys to enable these.");
    }
}
