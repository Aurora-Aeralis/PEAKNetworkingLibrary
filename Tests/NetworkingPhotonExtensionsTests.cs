using System;
using System.Reflection;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using Steamworks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetworkingPhotonExtensionsTests
{
    sealed class ThrowingLobbyMemberService : INetworkingService
    {
        public int GetLobbyMemberSteamIdsCalls { get; private set; }
        public bool IsInitialized => true;
        public bool InLobby => true;
        public ulong HostSteamId64 => 0;
        public string HostIdString => string.Empty;
        public bool IsHost => false;
        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        public event Action? LobbyCreated { add { } remove { } }
        public event Action? LobbyEntered { add { } remove { } }
        public event Action? LobbyLeft { add { } remove { } }
        public event Action<ulong>? PlayerEntered { add { } remove { } }
        public event Action<ulong>? PlayerLeft { add { } remove { } }
        public event Action<string[]>? LobbyDataChanged { add { } remove { } }
        public event Action<ulong, string[]>? PlayerDataChanged { add { } remove { } }

        public ulong GetLocalSteam64() => 0;
        public ulong[] GetLobbyMemberSteamIds()
        {
            GetLobbyMemberSteamIdsCalls++;
            throw new InvalidOperationException("Lobby members should not be queried outside a Photon room.");
        }
        public void Initialize() { }
        public void Shutdown() { }
        public void CreateLobby(int maxPlayers = 8) { }
        public void JoinLobby(ulong lobbySteamId64) { }
        public void LeaveLobby() { }
        public void InviteToLobby(ulong steamId64) { }
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0) => NullDisposable.Instance;
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0) => NullDisposable.Instance;
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0) { }
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) { }
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) { }
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters) { }
        public void RegisterLobbyDataKey(string key) { }
        public void SetLobbyData(string key, object value) { }
        public T GetLobbyData<T>(string key) => default!;
        public void RegisterPlayerDataKey(string key) { }
        public void SetPlayerData(string key, object value) { }
        public T GetPlayerData<T>(ulong steamId64, string key) => default!;
        public void PollReceive() { }
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) { }
        public void RegisterModPublicKey(uint modId, System.Security.Cryptography.RSAParameters pub) { }
    }

    sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    [Fact]
    public void MapPhotonActorsToSteam_DoesNotQueryService_WhenPhotonIsNotInRoom()
    {
        var service = new ThrowingLobbyMemberService();
        NetworkingPhotonExtensions.PhotonInRoom = () => false;
        NetworkingPhotonExtensions.CurrentPhotonRoom = () => throw new InvalidOperationException("Room should not be queried when Photon is not in-room.");
        try
        {
            var map = NetworkingPhotonExtensions.MapPhotonActorsToSteam(service);

            Assert.Empty(map);
            Assert.Equal(0, service.GetLobbyMemberSteamIdsCalls);
        }
        finally
        {
            NetworkingPhotonExtensions.ResetPhotonRoomStateForTests();
        }
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Throttles_Repeated_Issue_Within_Cooldown()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0));
        Assert.False(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0.AddSeconds(3)));
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0.AddSeconds(8)));
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_AllowsEmission_WhenClockMovesBackward()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "clock moved", t0.AddMinutes(1)));
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "clock moved", t0));
        Assert.False(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "clock moved", t0.AddSeconds(3)));
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Prunes_Old_Entries()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(1, "old issue", t0));
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(2, "active issue", t0.AddSeconds(1)));
        Assert.Equal(2, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());

        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(3, "trigger prune", t0.AddSeconds(29)));
        Assert.Equal(2, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Caps_Size_Under_Repeated_Unique_Keys()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3000; i++) Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(i, "issue_" + i, t0.AddSeconds(i)));

        Assert.True(NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests() <= 2048);
    }

    [Fact]
    public void ResetUnresolvedMappingWarningThrottleForTests_Resets_Prune_Scheduling_State()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(1, "old issue", t0));

        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(2, "new issue", t0.AddSeconds(29)));
        Assert.Equal(1, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());
    }

    [Fact]
    public void TryParseSteamId_Accepts_Valid_CSteamId_Ulong_Long_And_Numeric_String()
    {
        const ulong validSteam64 = 76561198000000000UL;

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(new CSteamID(validSteam64), out var fromSteamId));
        Assert.Equal(validSteam64, fromSteamId);

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(validSteam64, out var fromUlong));
        Assert.Equal(validSteam64, fromUlong);

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests((long)validSteam64, out var fromLong));
        Assert.Equal(validSteam64, fromLong);

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(validSteam64.ToString(), out var fromString));
        Assert.Equal(validSteam64, fromString);
    }

    [Theory]
    [InlineData("STEAM_0:1:12345", 76561197960290419UL)]
    [InlineData("STEAM_1:0:12345", 76561197960290418UL)]
    [InlineData(" [U:1:24691] ", 76561197960290419UL)]
    [InlineData("U:1:24690", 76561197960290418UL)]
    public void TryParseSteamId_Accepts_SteamAuthId_Formats(string authId, ulong expected)
    {
        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(authId, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("STEAM_2:1:12345")]
    [InlineData("STEAM_1:2:12345")]
    [InlineData("STEAM_1:1:notnum")]
    [InlineData("[U:2:24691]")]
    [InlineData("[I:1:24691]")]
    public void TryParseSteamId_Rejects_Unsupported_AuthId_Formats(string authId)
    {
        Assert.False(NetworkingPhotonExtensions.TryParseSteamIdForTests(authId, out _));
    }

    [Fact]
    public void TryParseSteamId_Rejects_Small_Numeric_Types()
    {
        Assert.False(NetworkingPhotonExtensions.TryParseSteamIdForTests(uint.MaxValue, out _));
        Assert.False(NetworkingPhotonExtensions.TryParseSteamIdForTests(765611980, out _));
    }

    [Fact]
    public void BuildPersonaLookupIndex_Preserves_Ambiguous_Name_Grouping()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        NetworkingPhotonExtensions.UtcNow = () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.PersonaNameLookup = sid => sid switch
        {
            76561198000000001UL => "Aurora",
            76561198000000002UL => "Aurora",
            76561198000000003UL => "Comet",
            _ => string.Empty
        };

        var lobbyIds = new[]
        {
            76561198000000001UL,
            76561198000000002UL,
            76561198000000003UL
        };

        var map = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, map["Aurora"].Count);
        Assert.Contains(76561198000000001UL, map["Aurora"]);
        Assert.Contains(76561198000000002UL, map["Aurora"]);
        Assert.Single(map["Comet"]);
        Assert.Contains(76561198000000003UL, map["Comet"]);
    }

    [Fact]
    public void BuildPersonaLookupIndex_Deduplicates_Repeated_LobbyIds()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        NetworkingPhotonExtensions.UtcNow = () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lookupCalls = 0;
        NetworkingPhotonExtensions.PersonaNameLookup = sid =>
        {
            lookupCalls++;
            return sid == 76561198000000001UL ? "Aurora" : "Comet";
        };

        var map = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(new[]
        {
            76561198000000001UL,
            76561198000000001UL,
            76561198000000003UL,
            76561198000000003UL
        });

        Assert.Equal(2, lookupCalls);
        Assert.Single(map["Aurora"]);
        Assert.Single(map["Comet"]);
        Assert.Contains(76561198000000001UL, map["Aurora"]);
        Assert.Contains(76561198000000003UL, map["Comet"]);
    }

    [Fact]
    public void BuildPersonaLookupIndex_Skips_Invalid_Offline_LobbyIds()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        NetworkingPhotonExtensions.UtcNow = () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lookupCalls = 0;
        NetworkingPhotonExtensions.PersonaNameLookup = sid =>
        {
            lookupCalls++;
            return sid == 76561198000000001UL ? "Aurora" : "Invalid";
        };

        var map = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(new[]
        {
            0UL,
            1000UL,
            76561198000000001UL
        });

        Assert.Equal(1, lookupCalls);
        Assert.Single(map);
        Assert.Single(map["Aurora"]);
        Assert.Contains(76561198000000001UL, map["Aurora"]);
    }

    [Fact]
    public void BuildPersonaLookupIndex_Uses_Cache_Within_Ttl_And_Refreshes_After_Expiry()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.UtcNow = () => now;

        var calls = 0;
        NetworkingPhotonExtensions.PersonaNameLookup = sid =>
        {
            calls++;
            return sid == 76561198000000001UL ? "Aurora" : "Comet";
        };

        var lobbyIds = new[]
        {
            76561198000000001UL,
            76561198000000002UL
        };

        var first = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, calls);
        Assert.Single(first["Aurora"]);
        Assert.Single(first["Comet"]);

        now = now.AddSeconds(2);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, calls);

        now = now.AddSeconds(4);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void BuildPersonaLookupIndex_RefreshesCachedPersonaName_WhenClockMovesBackward()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        var now = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        var calls = 0;
        NetworkingPhotonExtensions.UtcNow = () => now;
        NetworkingPhotonExtensions.PersonaNameLookup = _ => ++calls == 1 ? "Aurora" : "Aeralis";

        var lobbyIds = new[] { 76561198000000001UL };
        var first = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);

        now = now.AddMinutes(-1);
        var second = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);

        Assert.Equal(2, calls);
        Assert.Single(first["Aurora"]);
        Assert.Single(second["Aeralis"]);
        Assert.False(second.ContainsKey("Aurora"));
    }

    [Fact]
    public void BuildPersonaLookupIndex_Uses_InjectedClock_For_PersonaLookupExceptionThrottle()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.UtcNow = () => now;
        NetworkingPhotonExtensions.PersonaNameLookup = _ => throw new InvalidOperationException("steam unavailable");

        var lobbyIds = new[] { 76561198000000001UL };
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(now, ReadPrivateStaticField<DateTime>("lastPersonaLookupExceptionUtc"));

        now = now.AddSeconds(1);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(1, ReadPrivateStaticField<int>("suppressedPersonaLookupExceptions"));
    }

    [Fact]
    public void BuildPersonaLookupIndex_AllowsPersonaLookupWarning_WhenClockMovesBackward()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        var now = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.UtcNow = () => now;
        NetworkingPhotonExtensions.PersonaNameLookup = _ => throw new InvalidOperationException("steam unavailable");

        var lobbyIds = new[] { 76561198000000001UL };
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);

        now = now.AddMinutes(-1);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);

        Assert.Equal(now, ReadPrivateStaticField<DateTime>("lastPersonaLookupExceptionUtc"));
        Assert.Equal(0, ReadPrivateStaticField<int>("suppressedPersonaLookupExceptions"));
    }

    static T ReadPrivateStaticField<T>(string fieldName)
    {
        return (T)typeof(NetworkingPhotonExtensions)
            .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
    }

}
