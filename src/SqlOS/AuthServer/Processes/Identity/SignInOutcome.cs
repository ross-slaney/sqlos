using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>What a password sign-in did.</summary>
internal abstract record SignInOutcome
{
    private SignInOutcome()
    {
    }

    /// <summary>The credential proved who the person is, and the hub completed the login.</summary>
    public sealed record SignedIn(LoginEvidence Evidence, LoginCompletion Completion) : SignInOutcome;

    /// <summary>The sign-in was refused; nothing completed.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : SignInOutcome;
}
