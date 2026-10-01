using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// SqlOS issues, reads and spends its temporary tokens only through their kinds
/// (<c>SqlOSTemporaryTokenKinds</c>): the purpose-string API of <c>SqlOSCryptoService</c> is the
/// facade hosts use for purposes of their own, and only that facade itself may call it.
/// </summary>
[TestClass]
public sealed class TemporaryTokenKindUsageTests
{
    private const string CryptoService = "SqlOS.AuthServer.Services.SqlOSCryptoService";
    private static readonly string[] PurposeStringMembers =
        ["CreateTemporaryTokenAsync", "FindTemporaryTokenAsync", "ConsumeTemporaryTokenAsync", "DeserializePayload"];

    [TestMethod]
    public void Sqlos_never_uses_a_temporary_token_by_purpose_string()
    {
        var calls = IlScanner.SqlOS.Methods(type => IlScanner.TypeName(type) != CryptoService)
            .SelectMany(method => IlScanner.Calls(method)
                .Where(call => call.Instruction.OpCode.Code is Code.Call or Code.Callvirt
                    && IlScanner.TypeName(call.Target.DeclaringType) == CryptoService
                    && PurposeStringMembers.Contains(call.Target.Name)
                    && (call.Target.Name == "DeserializePayload"
                        || call.Target.Parameters.FirstOrDefault()?.ParameterType.FullName == "System.String"))
                .Select(call => $"{IlScanner.SourceMember(method)} -> {call.Target.Name}"))
            .Distinct()
            .ToList();

        calls.Should().BeEmpty("SqlOS passes a TemporaryTokenKind, never a purpose string");
    }
}
