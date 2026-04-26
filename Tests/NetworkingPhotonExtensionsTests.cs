using System.Collections.Generic;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetworkingPhotonExtensionsTests
{
    [Fact]
    public void ResolveStableIdentityCandidates_ReturnsNoStableDataPresent_WhenNoCandidates()
    {
        var result = NetworkingPhotonExtensions.ResolveStableIdentityCandidates(new ulong[0], new HashSet<ulong> { 111UL, 222UL });

        Assert.Equal(NetworkingPhotonExtensions.StableIdentityResolutionStatus.NoStableDataPresent, result.Status);
        Assert.False(result.IsResolved);
        Assert.Equal(0UL, result.MatchedSteamId);
    }

    [Fact]
    public void ResolveStableIdentityCandidates_ReturnsStableDataNoLobbyMatch_WhenCandidateOutsideLobby()
    {
        var result = NetworkingPhotonExtensions.ResolveStableIdentityCandidates(new[] { 333UL }, new HashSet<ulong> { 111UL, 222UL });

        Assert.Equal(NetworkingPhotonExtensions.StableIdentityResolutionStatus.StableDataNoLobbyMatch, result.Status);
        Assert.False(result.IsResolved);
        Assert.Equal(0UL, result.MatchedSteamId);
    }

    [Fact]
    public void ResolveStableIdentityCandidates_ReturnsResolved_WhenSingleLobbyMatch()
    {
        var result = NetworkingPhotonExtensions.ResolveStableIdentityCandidates(new[] { 333UL, 222UL }, new HashSet<ulong> { 111UL, 222UL });

        Assert.Equal(NetworkingPhotonExtensions.StableIdentityResolutionStatus.Resolved, result.Status);
        Assert.True(result.IsResolved);
        Assert.Equal(222UL, result.MatchedSteamId);
    }

    [Fact]
    public void AmbiguousStableIdentity_DisablesNicknameFallback_EvenWhenNicknameWouldMatchSingleLobbyPersona()
    {
        var result = NetworkingPhotonExtensions.ResolveStableIdentityCandidates(new[] { 111UL, 222UL }, new HashSet<ulong> { 111UL, 222UL, 333UL });

        Assert.Equal(NetworkingPhotonExtensions.StableIdentityResolutionStatus.AmbiguousStableMatch, result.Status);
        Assert.False(NetworkingPhotonExtensions.ShouldAllowNicknameFallback(result.Status));
        Assert.False(result.IsResolved);
        Assert.Equal(0UL, result.MatchedSteamId);
    }
}
