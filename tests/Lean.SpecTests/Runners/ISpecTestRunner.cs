namespace Lean.SpecTests.Runners;

public interface ISpecTestRunner
{
    void Run(string testId, string testJson);
}
