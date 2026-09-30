using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// The suite reads the behavior ledger to decide which approvals package mode skips. These tests
/// pin the parsing rule it shares with <c>scripts/check-behavior-ledger.sh</c>: only paths inside
/// a <c>### BL-NNNN: summary</c> section count.
/// </summary>
[TestClass]
public sealed class BehaviorLedgerTests
{
    [TestMethod]
    public void The_committed_ledger_lists_only_approved_files()
    {
        var ledger = BehaviorLedger.Load();

        foreach (var path in ledger.ListedPaths)
        {
            StringAssert.Matches(path, new System.Text.RegularExpressions.Regex(@"^(tests/SqlOS\.BehaviorLock/.+\.verified\.txt|packages/headless/tests/__snapshots__/.+)$"), $"{path} is not an approved file.");
        }
    }

    [TestMethod]
    public void Only_paths_inside_entry_sections_are_listed()
    {
        var ledger = BehaviorLedger.Parse("""
            # 8.0 behavior ledger

            Approved files such as `tests/SqlOS.BehaviorLock/PublicApi/SqlOS.verified.txt` are named in prose.

            ## Entry format

                ### BL-0001: Example in a code block

                - **Approved files:** `tests/SqlOS.BehaviorLock/Schema/SqlServer.verified.txt`

            ## Entries

            ### BL-0001: A real entry

            - **Approved files:** `tests/SqlOS.BehaviorLock/Scenarios/Dcr/Approved/A.verified.txt`, `packages/headless/tests/__snapshots__/public-surface.snap.txt`
            - **Category:** defect fixed

            ### Notes

            `tests/SqlOS.BehaviorLock/Scenarios/Dcr/Approved/B.verified.txt` is outside any entry.
            """);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "tests/SqlOS.BehaviorLock/Scenarios/Dcr/Approved/A.verified.txt",
                "packages/headless/tests/__snapshots__/public-surface.snap.txt"
            },
            ledger.ListedPaths.ToArray());
        Assert.IsTrue(ledger.Lists("tests/SqlOS.BehaviorLock/Scenarios/Dcr/Approved/A.verified.txt"));
        Assert.IsFalse(ledger.Lists("tests/SqlOS.BehaviorLock/Schema/SqlServer.verified.txt"));
    }
}
