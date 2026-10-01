using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using SqlOS.Tests.Architecture.Fixtures.Model;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// Runs every architecture rule on fixtures with known violations
/// (<c>Architecture/Fixtures</c>), so a rule that silently finds nothing fails here.
/// </summary>
[TestClass]
public sealed class ArchitectureRuleSelfTests
{
    private const string Fixtures = "SqlOS.Tests.Architecture.Fixtures";
    private const string Services = Fixtures + ".Services.FixtureTokenService";

    [TestMethod]
    public void Public_setters_are_found_including_init()
    {
        ArchitectureRules.PublicSetters([typeof(FixtureToken)]).Should().Equal(
            $"{Fixtures}.Model.FixtureToken.ConsumedAt",
            $"{Fixtures}.Model.FixtureToken.ExpiresAt",
            $"{Fixtures}.Model.FixtureToken.Name",
            $"{Fixtures}.Model.FixtureToken.Owner");
    }

    [TestMethod]
    public void Lifecycle_writes_from_outside_the_entity_are_found_and_folded_into_their_source_method()
    {
        var entities = new HashSet<string>(StringComparer.Ordinal) { $"{Fixtures}.Model.FixtureToken" };

        ArchitectureRules.LifecycleWrites(IlScanner.Tests, entities).Should().Equal(
            $"{Services}::BulkConsumeAsync -> FixtureToken.ConsumedAt (bulk update)",
            $"{Services}::Consume -> FixtureToken.ConsumedAt",
            $"{Services}::ExpireAsync -> FixtureToken.ExpiresAt");
    }

    [TestMethod]
    public void Adapters_that_touch_a_database_context_are_found()
    {
        ArchitectureRules.DatabaseAccess(IlScanner.Tests, type => InFixtures(type) && SqlOSCode.IsAdapter(type)).Should().Equal(
            $"{Fixtures}.Endpoints.FixtureEndpoints::CountLaterAsync",
            $"{Fixtures}.Endpoints.FixtureEndpoints::CountTokens");
    }

    [TestMethod]
    public void Saves_outside_processes_are_found()
    {
        ArchitectureRules.SaveCalls(IlScanner.Tests, type => !InFixtures(type) || SqlOSCode.IsProcess(type)).Should().Equal(
            $"{Services}::SaveAsync",
            $"{Services}::SaveLater");
    }

    [TestMethod]
    public void A_context_saving_through_its_base_is_not_a_call_site()
    {
        // SqlOSDbContext's overrides call base.SaveChanges; they are the save itself.
        ArchitectureRules.SaveCalls(IlScanner.SqlOS, SqlOSCode.IsProcess)
            .Should().NotContain(member => member.StartsWith("SqlOS.SqlOSDbContext`1::", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Proofs_built_outside_their_producers_are_found()
    {
        var producers = ProofProducers.Parse([$"{Fixtures}.Proofs.FixtureProof <- {Fixtures}.Proofs.FixtureProducer"]);

        ArchitectureRules.ProofViolations(IlScanner.Tests, producers, InFixtures, "SqlOS.Domain.ISqlOSProof").Should().Equal(
            $"{Fixtures}.Proofs.FixtureLeakyProof has a public or protected constructor",
            $"{Fixtures}.Proofs.FixtureLeakyProof is not sealed",
            $"{Fixtures}.Proofs.FixtureLeakyProof lists no producers",
            $"{Fixtures}.Proofs.FixtureLeakyProof::Create constructs {Fixtures}.Proofs.FixtureLeakyProof (proofs are constructed by their producers)",
            $"{Fixtures}.Proofs.LoginEvidence is a proof named by the design record but is not an SqlOS.Domain.ISqlOSProof",
            $"{Services}::Forge constructs {Fixtures}.Proofs.FixtureProof",
            $"{Services}::Rewrite copies {Fixtures}.Proofs.FixtureProof");
    }

    [TestMethod]
    public void Creation_without_the_proof_is_found()
    {
        ArchitectureRules.CreationWithoutProof(IlScanner.Tests, $"{Fixtures}.Proofs.FixtureGuarded", $"{Fixtures}.Proofs.FixtureProof").Should().Equal(
            $"{Fixtures}.Proofs.FixtureGuarded has a non-private constructor",
            $"{Fixtures}.Proofs.FixtureGuarded::Leak creates a FixtureGuarded without a {Fixtures}.Proofs.FixtureProof",
            $"{Fixtures}.Proofs.FixtureProducer::Bypass constructs {Fixtures}.Proofs.FixtureGuarded");
        ArchitectureRules.CreationWithoutProof(IlScanner.Tests, $"{Fixtures}.Proofs.Missing", $"{Fixtures}.Proofs.FixtureProof")
            .Should().Equal($"{Fixtures}.Proofs.Missing does not exist");
    }

    [TestMethod]
    public void A_listed_proof_that_does_not_exist_is_found()
    {
        var producers = ProofProducers.Parse([
            $"{Fixtures}.Proofs.FixtureProof <- {Fixtures}.Proofs.FixtureProducer",
            $"{Fixtures}.Proofs.FixtureLeakyProof <- {Fixtures}.Proofs.FixtureProducer",
            $"{Fixtures}.Proofs.Missing <- {Fixtures}.Proofs.FixtureProducer"]);

        ArchitectureRules.ProofViolations(IlScanner.Tests, producers, InFixtures)
            .Should().Contain($"{Fixtures}.Proofs.Missing is listed as a proof but is not an SqlOS.Domain.ISqlOSProof");
    }

    [TestMethod]
    public void Member_changes_outside_their_root_are_found()
    {
        var members = AggregateMembers.Parse([$"{Fixtures}.Aggregates.FixtureMember <- {Fixtures}.Aggregates.FixtureRoot"]);

        ArchitectureRules.MemberChangesOutsideTheirRoot(IlScanner.Tests, members).Should().Equal(
            $"{Fixtures}.Aggregates.FixtureMemberService::Forge -> FixtureMember::Create",
            $"{Fixtures}.Aggregates.FixtureMemberService::Rename -> FixtureMember::Rename");
    }

    [TestMethod]
    public void Clock_reads_in_domain_and_process_code_are_found()
    {
        ArchitectureRules.DomainClockReads(
                IlScanner.Tests,
                type => InFixtures(type) && SqlOSCode.IsDomainNamespace(type.Namespace),
                type => InFixtures(type) && SqlOSCode.IsProcess(type))
            .Should().Equal(
                $"{Fixtures}.Domain.FixtureDomainCode::FromProvider -> TimeProvider.GetUtcNow",
                $"{Fixtures}.Domain.FixtureDomainCode::FromProvider -> TimeProvider.System",
                $"{Fixtures}.Domain.FixtureDomainCode::Now -> DateTime.UtcNow",
                $"{Fixtures}.Processes.FixtureProcess::ReadSystemClock -> DateTime.Now");
    }

    [TestMethod]
    public void Http_dependencies_of_domain_code_are_found_in_members_and_fields()
    {
        ArchitectureRules.DomainHttpDependencies(IlScanner.Tests, type => InFixtures(type) && SqlOSCode.IsDomainNamespace(type.Namespace))
            .Should().Equal(
                $"{Fixtures}.Domain.FixtureDomainCode -> Microsoft.AspNetCore.Http.HttpContext",
                $"{Fixtures}.Domain.FixtureDomainCode::.ctor -> Microsoft.AspNetCore.Http.HttpContext",
                $"{Fixtures}.Domain.FixtureDomainCode::Path -> Microsoft.AspNetCore.Http.HttpContext",
                $"{Fixtures}.Domain.FixtureDomainCode::Path -> Microsoft.AspNetCore.Http.HttpRequest",
                $"{Fixtures}.Domain.FixtureDomainCode::Path -> Microsoft.AspNetCore.Http.PathString");
    }

    [DataTestMethod]
    [DataRow("SqlOS.Tests.Architecture.Fixtures.Services.FixtureTokenService", "ExpireAsync", "<ExpireAsync>d__2", "MoveNext")]
    [DataRow("SqlOS.Tests.Architecture.Fixtures.Services.FixtureTokenService", "SaveLater", "<>c__DisplayClass6_0", "<SaveLater>b__0")]
    public void Compiler_generated_members_fold_into_the_method_they_were_written_in(string type, string method, string nestedType, string nestedMethod)
    {
        var declaring = IlScanner.Tests.Module.GetType(type);
        var generated = declaring.NestedTypes.Single(nested => nested.Name == nestedType).Methods.Single(member => member.Name == nestedMethod);

        IlScanner.SourceMember(generated).Should().Be($"{type}::{method}");
    }

    private static bool InFixtures(TypeDefinition type)
        => type.Namespace.StartsWith(Fixtures, StringComparison.Ordinal);
}
