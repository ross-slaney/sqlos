namespace SqlOS.Domain;

/// <summary>
/// Marks a proof: a type that carries a security decision exactly one piece of code may make
/// (<c>LoginEvidence</c>, <c>OwnershipProof</c>, <c>LoginDecision</c>, <c>GrantAuthority</c>,
/// <c>DnsProof</c>). Consumers take the proof as a parameter, never a boolean or a string.
/// </summary>
/// <remarks>
/// A proof is <c>internal sealed</c> with no public constructor, and only its listed producers
/// construct it (or copy it with <c>with</c>). The architecture test
/// <c>Proofs_are_constructed_only_by_their_producers</c> enforces this against
/// <c>tests/SqlOS.Tests/Architecture/Allowlists/proof-producers.txt</c>, which names each proof's
/// producers.
/// </remarks>
internal interface ISqlOSProof
{
}
