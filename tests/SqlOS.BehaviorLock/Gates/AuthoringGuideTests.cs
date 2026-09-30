using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>Keeps the authoring guide in step with the harness it describes.</summary>
[TestClass]
public sealed class AuthoringGuideTests
{
    private static readonly string Guide = File.ReadAllText(RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "README.md"));

    [TestMethod]
    public void The_guide_lists_every_profile()
    {
        var missing = HostProfiles.All
            .Select(profile => profile.Name)
            .Where(name => !Guide.Contains($"| `{name}` |", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(0, missing.Count, $"tests/SqlOS.BehaviorLock/README.md does not describe these profiles: {string.Join(", ", missing)}");
    }
}
