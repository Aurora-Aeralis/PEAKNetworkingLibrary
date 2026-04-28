using System;
using NetworkingLibrary.Modules;

namespace NetworkingLibrary.Services
{
    public enum ReliableType { Unreliable, Reliable, UnreliableNoDelay }

    public interface INetworkingService
    {
        bool IsInitialized { get; }
        bool InLobby { get; }
        ulong HostSteamId64 { get; }
        string HostIdString { get; }
        bool IsHost { get; }
        ulong GetLocalSteam64();
        ulong[] GetLobbyMemberSteamIds();
        void Initialize();
        void Shutdown();

        void CreateLobby(int maxPlayers = 8);
        void JoinLobby(ulong lobbySteamId64);
        void LeaveLobby();
        void InviteToLobby(ulong steamId64);

        IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0);
        IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0);
        void DeregisterNetworkObject(object instance, uint modId, int mask = 0);
        void DeregisterNetworkType(Type type, uint modId, int mask = 0);

        void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters);

        void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters);

        void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters);

        void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters);

        void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters);

        void RegisterLobbyDataKey(string key);
        void SetLobbyData(string key, object value);
        T GetLobbyData<T>(string key);

        void RegisterPlayerDataKey(string key);
        void SetPlayerData(string key, object value);
        T GetPlayerData<T>(ulong steamId64, string key);

        void PollReceive();

        void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate);
        void RegisterModPublicKey(uint modId, System.Security.Cryptography.RSAParameters pub);

        event Action? LobbyCreated;
        event Action? LobbyEntered;
        event Action? LobbyLeft;
        event Action<ulong>? PlayerEntered;
        event Action<ulong>? PlayerLeft;
        event Action<string[]>? LobbyDataChanged;
        event Action<ulong, string[]>? PlayerDataChanged;

        Func<Message, ulong, bool>? IncomingValidator { get; set; }
    }
}
