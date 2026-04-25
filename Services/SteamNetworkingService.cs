#if !UNITY_EDITOR
using NetworkingLibrary.Modules;
using pworld.Scripts;
using Steamworks;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    /// <summary>
    /// </summary>
    public class SteamNetworkingService : INetworkingService
    {
        const int CHANNEL = 120;
        const int MAX_IN_MESSAGES = 500;
        static IntPtr[] inMessages = new IntPtr[MAX_IN_MESSAGES]; 
        private readonly object rpcLock = new object();
        private readonly object cryptoStateLock = new();

        /// <summary>
        /// </summary>
        public bool IsInitialized { get; private set; } = false;
        /// <summary>
        /// </summary>
        public bool InLobby { get; private set; } = false;
        /// <summary>
        /// </summary>
        public ulong HostSteamId64
        {
            get
            {
                if (Lobby == CSteamID.Nil) return 0UL;
                var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                if (owner == CSteamID.Nil) return 0UL;
                return owner.m_SteamID;
            }
        }
        /// <summary>
        /// </summary>
        public string HostIdString
        {
            get
            {
                if (Lobby == CSteamID.Nil) return string.Empty;
                var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                return owner == CSteamID.Nil ? string.Empty : owner.ToString();
            }
        }

        public ulong GetLocalSteam64()
        {
            try
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch
            {
                return 0UL;
            }
        }

        public ulong[] GetLobbyMemberSteamIds()
        {
            if (!InLobby || Lobby == CSteamID.Nil)
                return Array.Empty<ulong>();

            try
            {
                int count = getNumLobbyMembers(Lobby);
                if (count <= 0) return Array.Empty<ulong>();

                var memberIds = new List<ulong>(count);
                for (int i = 0; i < count; i++)
                {
                    var member = getLobbyMemberByIndex(Lobby, i);
                    if (member == CSteamID.Nil) continue;
                    memberIds.Add(member.m_SteamID);
                }
                return memberIds.Count == 0 ? Array.Empty<ulong>() : memberIds.ToArray();
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"GetLobbyMemberSteamIds error: {ex}");
                return Array.Empty<ulong>();
            }
        }


        /// <summary>
        /// </summary>
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        private CSteamID[] players = Array.Empty<CSteamID>();

        /// <summary>
        /// </summary>
        public bool IsHost
        {
            get
            {
                try
                {
                    if (!InLobby) return false;
                    var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
                    if (owner == CSteamID.Nil) return false;
                    return owner == SteamUser.GetSteamID();
                }
                catch
                {
                    return false;
                }
            }
        }
        /// <summary>
        /// </summary>
        public event Action? LobbyCreated;
        /// <summary>
        /// </summary>
        public event Action? LobbyEntered;
        /// <summary>
        /// </summary>
        public event Action? LobbyLeft;
        /// <summary>
        /// </summary>
        public event Action<ulong>? PlayerEntered;
        /// <summary>
        /// </summary>
        public event Action<ulong>? PlayerLeft;
        /// <summary>
        /// </summary>
        public event Action<string[]>? LobbyDataChanged;
        /// <summary>
        /// </summary>
        public event Action<ulong, string[]>? PlayerDataChanged;

        /// <summary>
        /// </summary>
        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        private readonly List<string> lobbyDataKeys = new();
        private readonly List<string> playerDataKeys = new();
        private readonly Dictionary<CSteamID, Dictionary<string, string>> lastPlayerData = new();
        private readonly Dictionary<string, string> lastLobbyData = new();
        private Func<CSteamID, int> getNumLobbyMembers = SteamMatchmaking.GetNumLobbyMembers;
        private Func<CSteamID, int, CSteamID> getLobbyMemberByIndex = SteamMatchmaking.GetLobbyMemberByIndex;

        Callback<LobbyEnter_t>? cbLobbyEnter;
        Callback<LobbyCreated_t>? cbLobbyCreated;
        Callback<LobbyChatUpdate_t>? cbLobbyChatUpdate;
        Callback<LobbyDataUpdate_t>? cbLobbyDataUpdate;

        readonly Dictionary<uint, Dictionary<string, List<MessageHandler>>> rpcs = new();

        readonly Queue<QueuedSend> highQueue = new();
        readonly Queue<QueuedSend> normalQueue = new();
        readonly Queue<QueuedSend> lowQueue = new();
        readonly object queueLock = new();

        readonly Dictionary<(ulong target, ulong msgId), UnackedMessage> unacked = new();
        readonly object unackedLock = new();
        TimeSpan ackTimeout = TimeSpan.FromSeconds(1.2);
        int maxRetransmitAttempts = 5;

        private long _nextMessageId = 0;
        private ulong NextMessageId() => (ulong)Interlocked.Increment(ref _nextMessageId);
        readonly Dictionary<uint, ulong> outgoingSequencePerMod = new();
        readonly Dictionary<ulong, Dictionary<uint, ulong>> lastSeenSequence = new();

        readonly Dictionary<ulong, SlidingWindowRateLimiter> rateLimiters = new();

        readonly Dictionary<ulong, byte[]> perPeerSymmetricKey = new();
        byte[]? globalSharedSecret = null;
        HMACSHA256? globalHmac = null;

        readonly Dictionary<uint, Func<byte[], byte[]>> modSigners = new();
        readonly Dictionary<uint, RSAParameters> modPublicKeys = new();
        static readonly HashSet<string> CompatibleRpcInfoTypeNames = new(StringComparer.Ordinal)
        {
            "NetworkingLibrary.Modules.RPCInfo"
        };

        readonly Dictionary<ulong, HandshakeState> handshakeStates = new();

        const byte FRAG_FLAG = 0x1;
        const byte COMPRESSED_FLAG = 0x2;
        const byte HMAC_FLAG = 0x4;
        const byte SIGN_FLAG = 0x8;
        const byte ACK_FLAG = 0x10;
        const int FRAME_HEADER_SIZE = 25; // flags[1] + msgId[8] + seq[8] + total[4] + index[4]

        RSACryptoServiceProvider? LocalRsa;
        private Func<RSACryptoServiceProvider> localRsaFactory = () => new RSACryptoServiceProvider(2048);

        /// <summary>
        /// </summary>
        public SteamNetworkingService() { }

        readonly Dictionary<(ulong sender, ulong msgId), FragmentBuffer> fragmentBuffers = new();
        readonly object fragmentLock = new();
        readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(30);

        class FragmentBuffer
        {
            public int Total;
            public DateTime FirstSeen = DateTime.UtcNow;
            public Dictionary<int, byte[]> Fragments = new();
        }

        sealed class HandlerRegistration
        {
            public string MethodName = string.Empty;
            public MessageHandler Handler = null!;
        }

        /// <summary>
        /// </summary>
        public void Initialize()
        {
            if (IsInitialized) return;

            if (Application.isPlaying)
            {
                try
                {
                    var go = GameObject.Find("SteamCallbackPump");
                    if (go == null)
                    {
                        go = new GameObject("SteamCallbackPump");
                        GameObject.DontDestroyOnLoad(go);
                        go.AddComponent<SteamCallbackPump>();
                        Net.Logger.LogInfo("Created SteamCallbackPump GameObject.");
                    }
                }
                catch (Exception ex)
                {
                    Net.Logger.LogError($"Failed to create SteamCallbackPump: {ex}");
                }
            }

            try
            {
                Message.MaxSize = (int)Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
            }
            catch { }

            if (!TryInitializeSteamCallbacksAndCrypto())
            {
                IsInitialized = false;
                return;
            }

            IsInitialized = true;
            Net.Logger.LogInfo("SteamNetworkingService initialized");
        }

        private bool TryInitializeSteamCallbacksAndCrypto()
        {
            try
            {
                cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                cbLobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
                LocalRsa = localRsaFactory();
                return true;
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"Failed to initialize Steam callbacks and crypto: {ex}");
                cbLobbyEnter = null;
                cbLobbyCreated = null;
                cbLobbyChatUpdate = null;
                cbLobbyDataUpdate = null;
                LocalRsa = null;
                return false;
            }
        }

        /// <summary>
        /// </summary>
        public void Shutdown()
        {
            if (InLobby || Lobby != CSteamID.Nil)
                LeaveLobby();

            cbLobbyEnter = null;
            cbLobbyCreated = null;
            cbLobbyChatUpdate = null;
            cbLobbyDataUpdate = null;

            ClearOutboundState();
            lobbyDataKeys.Clear();
            playerDataKeys.Clear();
            lastLobbyData.Clear();
            lastPlayerData.Clear();
            players = Array.Empty<CSteamID>();
            Lobby = CSteamID.Nil;
            InLobby = false;
            lock (cryptoStateLock)
            {
                handshakeStates.Clear();
                ClearPerPeerSymmetricKeysUnderLock();
                ClearGlobalSharedSecretUnderLock();
                globalHmac?.Dispose();
                globalHmac = null;
            }
            LocalRsa?.Dispose();
            LocalRsa = null;

            IsInitialized = false;
            lock (lastSeenSequence) lastSeenSequence.Clear();
            lock (rateLimiters) rateLimiters.Clear();
            lock (outgoingSequencePerMod) outgoingSequencePerMod.Clear();
            lock (fragmentLock) fragmentBuffers.Clear();
            Net.Logger.LogInfo("SteamNetworkingService shutdown");
        }

        /// <summary>
        /// </summary>
        public void CreateLobby(int maxPlayers = 8)
        {
            if (!IsInitialized)
            {
                Net.Logger?.LogError("CreateLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (maxPlayers <= 0)
            {
                Net.Logger.LogWarning($"CreateLobby maxPlayers {maxPlayers} is invalid. Clamping to 1.");
                maxPlayers = 1;
            }

            try { SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxPlayers); }
            catch (Exception ex) { Net.Logger.LogError($"CreateLobby failed: {ex}"); }
        }
        /// <summary>
        /// </summary>
        public void JoinLobby(ulong lobbySteamId64)
        {
            if (!IsInitialized)
            {
                Net.Logger?.LogError("JoinLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (lobbySteamId64 == 0UL)
            {
                Net.Logger.LogWarning("JoinLobby called with invalid lobby id 0.");
                return;
            }

            try { SteamMatchmaking.JoinLobby(new CSteamID(lobbySteamId64)); }
            catch (Exception ex) { Net.Logger.LogError($"JoinLobby failed for lobby {lobbySteamId64}: {ex}"); }
        }
        /// <summary>
        /// </summary>
        public void LeaveLobby()
        {
            if (!InLobby || Lobby == CSteamID.Nil) return;

            try
            {
                SteamMatchmaking.LeaveLobby(Lobby);
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"LeaveLobby failed for lobby {Lobby}: {ex}");
            }

            OnLobbyLeftInternal();
        }
        /// <summary>
        /// </summary>
        public void InviteToLobby(ulong steamId64)
        {
            if (!IsInitialized)
            {
                Net.Logger?.LogError("InviteToLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (!InLobby || Lobby == CSteamID.Nil)
            {
                Net.Logger.LogWarning("InviteToLobby called while not in a lobby.");
                return;
            }

            if (steamId64 == 0UL)
            {
                Net.Logger.LogWarning("InviteToLobby called with invalid target Steam64 id 0.");
                return;
            }

            try { SteamMatchmaking.InviteUserToLobby(Lobby, new CSteamID(steamId64)); }
            catch (Exception ex) { Net.Logger.LogError($"InviteToLobby failed for target {steamId64}: {ex}"); }
        }

        void OnLobbyEnter(LobbyEnter_t param)
        {
            Net.Logger.LogDebug($"LobbyEnter {param.m_ulSteamIDLobby}");
            Lobby = new CSteamID(param.m_ulSteamIDLobby);
            InLobby = true;
            RefreshPlayerList();
            LobbyEntered?.Invoke();
        }

        void OnLobbyCreated(LobbyCreated_t param)
        {
            Net.Logger.LogDebug($"LobbyCreated: {param.m_eResult}");
            if (param.m_eResult == EResult.k_EResultOK)
            {
                Lobby = new CSteamID(param.m_ulSteamIDLobby);
                InLobby = true;
                RefreshPlayerList();
                LobbyCreated?.Invoke();
            }
            else
            {
                Net.Logger.LogError($"Lobby creation failed: {param.m_eResult}");
            }
        }

        void OnLobbyChatUpdate(LobbyChatUpdate_t param)
        {
            try
            {
                RefreshPlayerList();
                var player = new CSteamID(param.m_ulSteamIDUserChanged);
                var change = (EChatMemberStateChange)param.m_rgfChatMemberStateChange;

                if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
                {
                    //Net.Logger.LogInfo($"OnLobbyChatUpdate: Entered -> {player}");
                    PlayerEntered?.Invoke(player.m_SteamID);
                }

                var leftMask =
                    EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                    EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                    EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                    EChatMemberStateChange.k_EChatMemberStateChangeBanned;

                if ((change & leftMask) != 0)
                {
                    //Net.Logger.LogInfo($"OnLobbyChatUpdate: Left/Disconnected/Kicked/Banned -> {player}");
                    PlayerLeft?.Invoke(player.m_SteamID);
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"OnLobbyChatUpdate error: {ex}");
            }
        }

        internal void OnLobbyLeftInternal()
        {
            players = Array.Empty<CSteamID>();
            lastLobbyData.Clear();
            lastPlayerData.Clear();
            Lobby = CSteamID.Nil;
            InLobby = false;

            ClearOutboundState();
            lock (lastSeenSequence) lastSeenSequence.Clear();
            lock (rateLimiters) rateLimiters.Clear();
            lock (fragmentLock) fragmentBuffers.Clear();
            lock (cryptoStateLock)
            {
                handshakeStates.Clear();
                ClearPerPeerSymmetricKeysUnderLock();
                ClearGlobalSharedSecretUnderLock();
                globalHmac?.Dispose();
                globalHmac = null;
            }

            LobbyLeft?.Invoke();
        }

        void ClearPerPeerSymmetricKeys()
        {
            lock (cryptoStateLock)
            {
                ClearPerPeerSymmetricKeysUnderLock();
            }
        }

        void ClearPerPeerSymmetricKeysUnderLock()
        {
            foreach (var key in perPeerSymmetricKey.Values)
            {
                if (key == null) continue;
                CryptographicOperations.ZeroMemory(key);
            }
            perPeerSymmetricKey.Clear();
        }

        void ClearGlobalSharedSecret()
        {
            lock (cryptoStateLock)
            {
                ClearGlobalSharedSecretUnderLock();
            }
        }

        void ClearGlobalSharedSecretUnderLock()
        {
            if (globalSharedSecret == null) return;
            CryptographicOperations.ZeroMemory(globalSharedSecret);
            globalSharedSecret = null;
        }

        void ClearOutboundState()
        {
            lock (queueLock)
            {
                highQueue.Clear();
                normalQueue.Clear();
                lowQueue.Clear();

                lock (unackedLock)
                {
                    unacked.Clear();
                }
            }
        }

        void RefreshPlayerList()
        {
            try
            {
                //Net.Logger.LogInfo($"RefreshPlayerList: Lobby={Lobby} Owner={SteamMatchmaking.GetLobbyOwner(Lobby)} Local={SteamUser.GetSteamID()} InLobby={InLobby}");

                if (Lobby == null || Lobby == CSteamID.Nil)
                {
                    //Net.Logger.LogWarning("RefreshPlayerList: Lobby is Nil; cannot query members.");
                    players = Array.Empty<CSteamID>();
                    return;
                }

                int count = SteamMatchmaking.GetNumLobbyMembers(Lobby);
                //Net.Logger.LogInfo($"RefreshPlayerList: SteamMatchmaking.GetNumLobbyMembers returned {count}");
                players = new CSteamID[count];

                for (int i = 0; i < players.Length; i++)
                {
                    players[i] = SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i);
                    //Net.Logger.LogInfo($"RefreshPlayerList: member[{i}] = {players[i]}");
                }
                Net.Logger.LogDebug($"RefreshPlayerList: total members = {players.Length}");
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"RefreshPlayerList error: {ex}");
                players = Array.Empty<CSteamID>();
            }
        }

        void OnLobbyDataUpdate(LobbyDataUpdate_t param)
        {
            if (!InLobby) return;
            if (param.m_ulSteamIDLobby != Lobby.m_SteamID) return;

            if (param.m_ulSteamIDLobby == param.m_ulSteamIDMember)
            {
                var changed = new List<string>();
                foreach (var key in lobbyDataKeys)
                {
                    var data = SteamMatchmaking.GetLobbyData(Lobby, key);
                    if (!lastLobbyData.TryGetValue(key, out var prev) || prev != data)
                    {
                        changed.Add(key);
                        lastLobbyData[key] = data;
                    }
                }
                if (changed.Count > 0) LobbyDataChanged?.Invoke(changed.ToArray());
            }
            else
            {
                var player = new CSteamID(param.m_ulSteamIDMember);
                if (!lastPlayerData.ContainsKey(player)) lastPlayerData[player] = new Dictionary<string, string>();
                var changed = new List<string>();
                foreach (var key in playerDataKeys)
                {
                    var data = SteamMatchmaking.GetLobbyMemberData(Lobby, player, key);
                    if (!lastPlayerData[player].TryGetValue(key, out var prev) || prev != data)
                    {
                        changed.Add(key);
                        lastPlayerData[player][key] = data;
                    }
                }
                if (changed.Count > 0) PlayerDataChanged?.Invoke(player.m_SteamID, changed.ToArray());
            }
        }

        /// <summary>
        /// </summary>
        public void RegisterLobbyDataKey(string key)
        {
            if (lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Lobby key {key} already registered");
            else lobbyDataKeys.Add(key);
        }

        /// <summary>
        /// </summary>
        public void SetLobbyData(string key, object value)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot set lobby data when not in lobby."); return; }
            if (!lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered lobby key '{key}'.");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            SteamMatchmaking.SetLobbyData(Lobby, key, serialized);
        }

        /// <summary>
        /// </summary>
        public T GetLobbyData<T>(string key)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot get lobby data when not in lobby."); return default(T)!; }
            if (!lobbyDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered lobby key '{key}'.");
            string v = SteamMatchmaking.GetLobbyData(Lobby, key);
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Net.Logger.LogError($"Could not parse lobby data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        /// <summary>
        /// </summary>
        public void RegisterPlayerDataKey(string key)
        {
            if (playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Player key {key} already registered");
            else playerDataKeys.Add(key);
        }

        /// <summary>
        /// </summary>
        public void SetPlayerData(string key, object value)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot set player data when not in lobby."); return; }
            if (!playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered player key '{key}'.");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            SteamMatchmaking.SetLobbyMemberData(Lobby, key, serialized);
        }

        /// <summary>
        /// </summary>
        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot get player data when not in lobby."); return default(T)!; }
            if (!playerDataKeys.Contains(key)) Net.Logger.LogWarning($"Accessing unregistered player key '{key}'.");
            var player = new CSteamID(steamId64);
            string v = SteamMatchmaking.GetLobbyMemberData(Lobby, player, key);
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Net.Logger.LogError($"Could not parse player data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return RegisterNetworkTypeInternal(instance.GetType(), instance, modId, mask);
        }
        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return RegisterNetworkTypeInternal(type, null, modId, mask);
        }
        private IDisposable RegisterNetworkTypeInternal(Type type, object? instance, uint modId, int mask)
        {
            int registered = 0;
            var registeredHandlers = new List<HandlerRegistration>();
            var registrationFlags = instance == null
                ? BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                : BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            lock (rpcLock)
            {
                if (instance == null)
                {
                    var instanceRpc = type
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(method => method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().Any());
                    if (instanceRpc != null) throw new InvalidOperationException($"Cannot register instance RPC method {type.FullName}.{instanceRpc.Name} without an instance.");
                }

                var methods = type.GetMethods(registrationFlags);
                foreach (var method in methods)
                {
                    var attrs = method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().ToArray();
                    if (attrs.Length == 0) continue;

                    if (!rpcs.ContainsKey(modId)) rpcs[modId] = new Dictionary<string, List<MessageHandler>>();
                    if (!rpcs[modId].ContainsKey(method.Name)) rpcs[modId][method.Name] = new List<MessageHandler>();
                    var handlers = rpcs[modId][method.Name];
                    // Preserve historical instance registration fan-out semantics: repeated
                    // RegisterNetworkObject calls for the same receiver should add another slot.
                    // Only static/type registrations are deduplicated.
                    var alreadyRegisteredStatic = instance == null
                        && handlers.Any(existing => existing.Mask == mask && existing.Method == method);
                    if (alreadyRegisteredStatic) continue;

                    var mh = new MessageHandler
                    {
                        Target = method.IsStatic ? null! : instance!,
                        Method = method,
                        Parameters = method.GetParameters(),
                        TakesInfo = method.GetParameters().Length > 0 && IsRpcInfoParameterType(method.GetParameters().Last().ParameterType),
                        Mask = mask
                    };
                    handlers.Add(mh);
                    registeredHandlers.Add(new HandlerRegistration { MethodName = method.Name, Handler = mh });
                    registered++;
                }
            }

            if (instance != null)
                Net.Logger.LogInfo($"Registered {registered} RPCs for mod {modId} on {instance} ({instance.GetType().FullName})");
            else
                Net.Logger.LogInfo($"Registered {registered} static RPCs for mod {modId} on type {type.FullName}");

            return new RegistrationToken(this, modId, registeredHandlers);
        }

        /// <summary>
        /// </summary>
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            DeregisterNetworkObjectInternal(instance.GetType(), instance, modId, mask);
        }
        /// <summary>
        /// </summary>
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            DeregisterNetworkObjectInternal(type, null, modId, mask);
        }
        private void DeregisterNetworkObjectInternal(Type type, object? instanceOrNull, uint modId, int mask)
        {
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(modId, out var methods))
                {
                    Net.Logger.LogWarning($"No RPCs for mod {modId}");
                    return;
                }

                int removed = 0;
                var methodNames = methods.Keys.ToArray();
                foreach (var methodName in methodNames)
                {
                    if (!methods.TryGetValue(methodName, out var handlers)) continue;

                    for (int i = handlers.Count - 1; i >= 0; i--)
                    {
                        var mh = handlers[i];

                        if (instanceOrNull != null && mh.Target != null && ReferenceEquals(mh.Target, instanceOrNull) && mh.Mask == mask)
                        {
                            handlers.RemoveAt(i);
                            removed++;
                            continue;
                        }
                        if (instanceOrNull == null && mh.Target == null && mh.Method.DeclaringType == type && mh.Mask == mask)
                        {
                            handlers.RemoveAt(i);
                            removed++;
                            continue;
                        }
                    }

                    if (handlers.Count == 0) methods.Remove(methodName);
                }

                if (methods.Count == 0) rpcs.Remove(modId);

                Net.Logger.LogInfo($"Deregistered {removed} RPCs for mod {modId} (type/instance {type.FullName})");
            }
        }

        private void DeregisterHandlers(uint modId, List<HandlerRegistration> handlersToRemove)
        {
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(modId, out var methods) || handlersToRemove.Count == 0) return;
                foreach (var registration in handlersToRemove)
                {
                    if (!methods.TryGetValue(registration.MethodName, out var handlers)) continue;
                    handlers.Remove(registration.Handler);
                    if (handlers.Count == 0) methods.Remove(registration.MethodName);
                }
                if (methods.Count == 0) rpcs.Remove(modId);
            }
        }

        sealed class RegistrationToken : IDisposable
        {
            private readonly SteamNetworkingService svc;
            private readonly uint modId;
            private readonly List<HandlerRegistration> handlers;
            private bool disposed;

            public RegistrationToken(SteamNetworkingService svc, uint modId, List<HandlerRegistration> handlers)
            {
                this.svc = svc;
                this.modId = modId;
                this.handlers = handlers;
                this.disposed = false;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                svc.DeregisterHandlers(modId, handlers);
            }
        }

        /// <summary>
        /// </summary>
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;

            //Net.Logger.LogError($"{players}");
            foreach (var p in players)
            {
                //Net.Logger.LogError($"{p}");
                if (p == SteamUser.GetSteamID())
                {
                    //Net.Logger.LogError($"{SteamUser.GetSteamID()} == {p}");
                    continue;
                }
                EnqueueOrSend(BuildFramedBytesWithMeta(msg, modId, reliable), p, reliable, DeterminePriority(modId, methodName));
                //Net.Logger.LogError($"Fired to user: {p}");
            }

            InvokeLocalMessage(new Message(msg.ToArray()), SteamUser.GetSteamID());
        }

        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;

            //Net.Logger.LogError($"{players}");
            foreach (var p in players)
            {
                //Net.Logger.LogError($"{p}");
                if (p == SteamUser.GetSteamID())
                {
                    //Net.Logger.LogError($"{SteamUser.GetSteamID()} == {p}");
                    continue;
                }
                EnqueueOrSend(BuildFramedBytesWithMeta(msg, modId, reliable), p, reliable, DeterminePriority(modId, methodName));
                //Net.Logger.LogError($"Fired to user: {p}");
            }

            InvokeLocalMessage(new Message(msg.ToArray()), SteamUser.GetSteamID());
        }

        /// <summary>
        /// </summary>
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters)
        {
            RPCTarget(modId, methodName, new CSteamID(targetSteamId64), reliable, parameters);
        }

        /// <summary>
        /// </summary>
        public void RPCTarget(uint modId, string methodName, CSteamID target, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            if (target == SteamUser.GetSteamID())
            {
                InvokeLocalMessage(new Message(msg.ToArray()), SteamUser.GetSteamID());
                return;
            }
            var framed = BuildFramedBytesWithMeta(msg, modId, reliable);
            EnqueueOrSend(framed, target, reliable, DeterminePriority(modId, methodName));
        }

        public void RPCTarget(uint modId, string methodName, CSteamID target, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            if (target == SteamUser.GetSteamID())
            {
                InvokeLocalMessage(new Message(msg.ToArray()), SteamUser.GetSteamID());
                return;
            }
            var framed = BuildFramedBytesWithMeta(msg, modId, reliable);
            EnqueueOrSend(framed, target, reliable, DeterminePriority(modId, methodName));
        }

        /// <summary>
        /// </summary>
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Not in lobby"); return; }
            var host = SteamMatchmaking.GetLobbyOwner(Lobby);
            if (host == CSteamID.Nil) { Net.Logger.LogError("No host set"); return; }
            RPCTarget(modId, methodName, host, reliable, parameters);
        }

        Priority DeterminePriority(uint modId, string methodName)
        {
            var lower = methodName.ToLowerInvariant();
            if (lower.Contains("admin") || lower.Contains("control") || lower.Contains("critical") || lower.Contains("sync")) return Priority.High;
            return Priority.Normal;
        }

        void EnqueueOrSend(byte[] framed, CSteamID target, ReliableType reliable, Priority p)
        {
            var rl = GetOrCreateRateLimiter(target.m_SteamID);
            if (!rl.Allowed())
            {
                //Net.Logger.LogWarning($"Rate limit: dropping send to {target}");
                return;
            }

            if (p == Priority.High)
            {
                SendWithPossibleAck(framed, target, reliable);
                return;
            }

            lock (queueLock)
            {
                var q = p == Priority.Normal ? normalQueue : lowQueue;
                q.Enqueue(new QueuedSend { Framed = framed, Target = target, Reliable = reliable, Enqueued = DateTime.UtcNow });
            }
        }

        void FlushQueues(int maxPerFrame = 8)
        {
            if (!InLobby) return;
            int sent = 0;
            while (sent < maxPerFrame)
            {
                QueuedSend item = null!;
                lock (queueLock)
                {
                    if (normalQueue.Count > 0) item = normalQueue.Dequeue();
                    else if (lowQueue.Count > 0) item = lowQueue.Dequeue();
                    else break;
                }
                if (item != null)
                {
                    SendWithPossibleAck(item.Framed, item.Target, item.Reliable);
                    sent++;
                }
            }
        }

        static void WriteU16LE(Stream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        static void WriteI32LE(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        static void WriteU64LE(Stream stream, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        byte[] BuildFramedBytesWithMeta(Message msg, uint modId, ReliableType reliable)
        {
            var payload = msg.ToArray();
            bool compress = payload.Length > 1024;
            if (compress) payload = msg.CompressPayload(); // Uncertain of this messes up everything or not, did not test this.

            byte flags = 0;
            if (compress) flags |= COMPRESSED_FLAG;
            bool hasGlobalHmac;
            byte[]? globalMacKey;
            Func<byte[], byte[]>? signer;
            lock (cryptoStateLock)
            {
                hasGlobalHmac = globalHmac != null;
                globalMacKey = globalSharedSecret != null ? (byte[])globalSharedSecret.Clone() : null;
                signer = modSigners.TryGetValue(modId, out var localSigner) ? localSigner : null;
            }
            try
            {
                if (hasGlobalHmac) flags |= HMAC_FLAG;
                if (signer != null) flags |= SIGN_FLAG;
                if (reliable == ReliableType.Reliable) flags |= ACK_FLAG;

                ulong seq;
                lock (outgoingSequencePerMod)
                {
                    if (!outgoingSequencePerMod.TryGetValue(modId, out var cur)) cur = 0;
                    seq = ++cur;
                    outgoingSequencePerMod[modId] = cur;
                }

                ulong msgId = NextMessageId();

                using var ms = new MemoryStream();
                // Frame layout:
                // [flags:1][msgId:8][seq:8][total:4][index:4][payload...][optional signature len:2 + signature...][optional global mac:32]
                // Canonical MAC scope is every byte from flags through payload/signature (everything except the trailing MAC that is being appended).
                ms.WriteByte(flags);
                WriteU64LE(ms, msgId);
                WriteU64LE(ms, seq);
                WriteI32LE(ms, 1);
                WriteI32LE(ms, 0);
                ms.Write(payload, 0, payload.Length);

                byte[] headerAndPayload = ms.ToArray();

                if (signer != null)
                {
                    var sig = signer(headerAndPayload);
                    using var ms2 = new MemoryStream();
                    ms2.Write(headerAndPayload, 0, headerAndPayload.Length);
                    var len = (ushort)sig.Length;
                    WriteU16LE(ms2, len);
                    ms2.Write(sig, 0, sig.Length);
                    headerAndPayload = ms2.ToArray();
                }

                if (hasGlobalHmac && globalMacKey != null)
                {
                    var mac = HmacSha256RawStatic(globalMacKey, headerAndPayload);
                    using var ms3 = new MemoryStream();
                    ms3.Write(headerAndPayload, 0, headerAndPayload.Length);
                    ms3.Write(mac, 0, mac.Length);
                    headerAndPayload = ms3.ToArray();
                }

                return headerAndPayload;
            }
            finally
            {
                if (globalMacKey != null)
                {
                    CryptographicOperations.ZeroMemory(globalMacKey);
                }
            }
        }

        void SendWithPossibleAck(byte[] framed, CSteamID target, ReliableType reliable)
        {
            byte[]? sym;
            lock (cryptoStateLock)
            {
                sym = perPeerSymmetricKey.TryGetValue(target.m_SteamID, out var localSym) ? (byte[])localSym.Clone() : null;
            }
            if (sym != null)
            {
                try
                {
                    framed[0] = (byte)(framed[0] | HMAC_FLAG);
                    framed = AppendFrameMac(framed, sym);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sym);
                }
            }

            bool requestAck = (framed[0] & ACK_FLAG) != 0;

            if (requestAck)
            {
                var msgId = BinaryPrimitives.ReadUInt64LittleEndian(framed.AsSpan(1, 8));
                var key = (target.m_SteamID, msgId);
                lock (unackedLock)
                {
                    unacked[key] = new UnackedMessage { Framed = framed, Target = target, Reliable = reliable, LastSent = DateTime.UtcNow, Attempts = 1 };
                }
            }

            SendBytes(framed, target, reliable);
        }
        void SendBytes(byte[] data, CSteamID target, ReliableType reliable)
        {
            if (data.Length > Message.MaxSize)
            {
                Net.Logger.LogError($"Send length {data.Length} exceeds Message.MaxSize {Message.MaxSize}");
                return;
            }

            if (target == SteamUser.GetSteamID())
            {
                var m = new Message(data);
                InvokeLocalMessage(m, SteamUser.GetSteamID());
                return;
            }

            var id = new SteamNetworkingIdentity();
            id.SetSteamID(target);
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = pinned.AddrOfPinnedObject();

                int flags = Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                switch (reliable)
                {
                    case ReliableType.Unreliable: flags |= Constants.k_nSteamNetworkingSend_Unreliable; break;
                    case ReliableType.Reliable: flags |= Constants.k_nSteamNetworkingSend_Reliable; break;
                    case ReliableType.UnreliableNoDelay: flags |= Constants.k_nSteamNetworkingSend_UnreliableNoDelay; break;
                }

                var res = SteamNetworkingMessages.SendMessageToUser(ref id, p, (uint)data.Length, flags, CHANNEL);
                //Net.Logger.LogInfo($"SendMessageToUser -> res={res} to={target} framedLen={data.Length}");
                if (res != EResult.k_EResultOK)
                {
                    Net.Logger.LogError($"SendMessageToUser failed: {res} to {target}");
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"SendBytes exception: {ex}");
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }
        }

        /// <summary>
        /// </summary>
        public void PollReceive()
        {
            FlushQueues();
            RetransmitUnacked();
            ReceiveMessages();
        }

        void RetransmitUnacked()
        {
            var toRetransmit = new List<UnackedMessage>();
            lock (unackedLock)
            {
                var now = DateTime.UtcNow;
                var keys = unacked.Keys.ToArray();
                foreach (var key in keys)
                {
                    var info = unacked[key];
                    if (now - info.LastSent > ackTimeout)
                    {
                        if (info.Attempts >= maxRetransmitAttempts)
                        {
                            unacked.Remove(key);
                            //Net.Logger.LogDebug($"RetransmitUnacked: Message {key.msgId} to {key.target} dropped after {info.Attempts} attempts. framedLen={info.Framed?.Length ?? 0}");
                        }
                        else
                        {
                            info.Attempts++;
                            info.LastSent = now;
                            unacked[key] = info;
                            toRetransmit.Add(info);
                            //Net.Logger.LogDebug($"RetransmitUnacked: scheduling retransmit attempt {info.Attempts} for msg {key.msgId} to {key.target}");
                        }
                    }
                }
            }

            foreach (var item in toRetransmit)
            {
                try
                {
                    SendBytes(item.Framed, item.Target, item.Reliable);
                }
                catch (Exception ex)
                {
                    Net.Logger.LogError($"RetransmitUnacked: retransmit SendBytes exception: {ex}");
                }
            }
        }

        void ReceiveMessages()
        {
            try
            {
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(CHANNEL, inMessages, MAX_IN_MESSAGES);
                //if (count > 0) Net.Logger.LogInfo($"ReceiveMessages: count={count} (channel {CHANNEL})");
                if (count <= 0) return;

                for (int i = 0; i < count; i++)
                {
                    IntPtr outPtr = inMessages[i];
                    SteamNetworkingMessage_t steamMsg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(outPtr);
                    int size = (int)steamMsg.m_cbSize;

                    //Net.Logger.LogInfo($"ReceiveMessages: rawMsg[{i}] size={size} ptr={outPtr}");

                    if (size <= 0)
                    {
                        //Net.Logger.LogWarning("ReceiveMessages: msg size <= 0, releasing.");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    if (size > Message.MaxSize)
                    {
                        //Net.Logger.LogError($"Incoming message size {size} > max {Message.MaxSize} (dropping)");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    CSteamID sender = steamMsg.m_identityPeer.GetSteamID();
                    if (sender == CSteamID.Nil)
                    {
                        //Net.Logger.LogWarning("ReceiveMessages: sender is Nil - skipping");
                        SteamNetworkingMessage_t.Release(outPtr);
                        continue;
                    }

                    byte[] bytes = new byte[size];
                    Marshal.Copy(steamMsg.m_pData, bytes, 0, size);

                    /*int dumpLen = Math.Min(32, bytes.Length);
                    var sb = new System.Text.StringBuilder();
                    for (int b = 0; b < dumpLen; b++) sb.AppendFormat("{0:X2} ", bytes[b]);
                    Net.Logger.LogInfo($"ReceiveMessages: from={sender} size={size} preview={sb}");*/
                    try
                    {
                        ProcessIncomingFrame(bytes, sender);
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame exception: {ex}");
                    }

                    SteamNetworkingMessage_t.Release(outPtr);
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"ReceiveMessages outer exception: {ex}");
            }
        }

        void ProcessIncomingFrame(byte[] frame, CSteamID sender)
        {
            //Net.Logger.LogInfo($"ProcessIncomingFrame: from={sender} bytes={frame.Length}");
            // Expected frame layout mirrors BuildFramedBytesWithMeta.
            // MAC verification always runs against canonical scope [flags..payload/signature], before any payload mutation/stripping.

            if (frame.Length < FRAME_HEADER_SIZE) return;
            int flags = frame[0];
            bool compressed = (flags & COMPRESSED_FLAG) != 0;
            bool hasHmac = (flags & HMAC_FLAG) != 0;
            bool hasSign = (flags & SIGN_FLAG) != 0;
            bool requiresAck = (flags & ACK_FLAG) != 0;

            if (hasHmac && !TryVerifyAndStripFrameMacs(frame, sender.m_SteamID, out frame))
            {
                return;
            }

            using var msHeader = new MemoryStream(frame);
            flags = msHeader.ReadByte();
            if (flags < 0)
            {
                //Net.Logger.LogWarning("ProcessIncomingFrame: flags read < 0");
                return;
            }

            try
            {
                ulong msgId = ReadU64(msHeader);
                ulong seq = ReadU64(msHeader);
                int total = ReadI32(msHeader);
                int index = ReadI32(msHeader);

                if (total < 1 || index < 0 || index >= total)
                {
                    Net.Logger.LogWarning($"ProcessIncomingFrame: malformed fragment header total={total} index={index} from {sender}");
                    return;
                }

                //Net.Logger.LogInfo($"ProcessIncomingFrame: header flags=0x{flags:X2} msgId={msgId} seq={seq} total={total} idx={index}");

                int remainingHeader = (int)(msHeader.Length - msHeader.Position);
                if (remainingHeader <= 0)
                {
                    //Net.Logger.LogWarning("ProcessIncomingFrame: Empty payload received (remainingHeader<=0)");
                    return;
                }

                var payloadFragment = new byte[remainingHeader];
                msHeader.Read(payloadFragment, 0, remainingHeader);

                byte[] assembledPayload;
                if (total > 1)
                {
                    var key = (sender.m_SteamID, msgId);
                    FragmentBuffer fb;
                    lock (fragmentLock)
                    {
                        if (!fragmentBuffers.TryGetValue(key, out fb))
                        {
                            fb = new FragmentBuffer { Total = total, FirstSeen = DateTime.UtcNow };
                            fragmentBuffers[key] = fb;
                        }

                        fb.Fragments[index] = payloadFragment;
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: stored fragment {index}/{total - 1} for key {sender}:{msgId} (fragments={fb.Fragments.Count})");

                        var stale = fragmentBuffers.Where(kv => DateTime.UtcNow - kv.Value.FirstSeen > FragmentTimeout)
                                                  .Select(kv => kv.Key).ToList();
                        foreach (var k in stale)
                        {
                            //Net.Logger.LogWarning($"ProcessIncomingFrame: removing stale fragment buffer for key {k}");
                            fragmentBuffers.Remove(k);
                        }

                        if (fb.Fragments.Count != fb.Total)
                        {
                            //Net.Logger.LogInfo($"ProcessIncomingFrame: waiting for more fragments ({fb.Fragments.Count}/{fb.Total})");
                            return;
                        }

                        using var outMs = new MemoryStream();
                        for (int i = 0; i < fb.Total; i++)
                        {
                            if (!fb.Fragments.TryGetValue(i, out var part))
                            {
                                //Net.Logger.LogWarning($"ProcessIncomingFrame: missing fragment {i}; discarding buffer for key {key}");
                                fragmentBuffers.Remove(key);
                                return;
                            }
                            outMs.Write(part, 0, part.Length);
                        }
                        assembledPayload = outMs.ToArray();
                        fragmentBuffers.Remove(key);
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: reassembled payload len={assembledPayload.Length} for key {sender}:{msgId}");
                    }
                }
                else
                {
                    assembledPayload = payloadFragment;
                }

                byte[] payloadWithOptionalMacAndSig = assembledPayload;
                byte[] payloadToProcess = payloadWithOptionalMacAndSig;

                if (hasSign)
                {
                    if (payloadToProcess.Length < 5)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small");
                        return;
                    }

                    if (payloadToProcess.Length < 1 + 4)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small to contain ModID");
                        return;
                    }

                    uint modId = BinaryPrimitives.ReadUInt32LittleEndian(payloadToProcess.AsSpan(1, 4));
                    RSAParameters rsaParams;
                    lock (cryptoStateLock)
                    {
                        if (!modPublicKeys.TryGetValue(modId, out rsaParams))
                        {
                            //Net.Logger.LogWarning($"ProcessIncomingFrame: No public key registered for mod {modId}; dropping signed msg");
                            return;
                        }
                    }

                    int expectedSigLen = rsaParams.Modulus?.Length ?? 0;
                    if (expectedSigLen <= 0 || payloadToProcess.Length < expectedSigLen + 2)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signed payload too small for expected signature length");
                        return;
                    }

                    int sigSectionStart = payloadToProcess.Length - expectedSigLen - 2;
                    if (sigSectionStart < 0)
                    {
                        //Net.Logger.LogWarning("ProcessIncomingFrame: Signature section invalid");
                        return;
                    }

                    ushort declaredLen = BinaryPrimitives.ReadUInt16LittleEndian(payloadToProcess.AsSpan(sigSectionStart, 2));
                    if (declaredLen != expectedSigLen)
                    {
                        //Net.Logger.LogWarning($"ProcessIncomingFrame: Signature length mismatch (declared={declaredLen}, expected={expectedSigLen}); dropping");
                        return;
                    }

                    var signature = new byte[expectedSigLen];
                    Array.Copy(payloadToProcess, sigSectionStart + 2, signature, 0, expectedSigLen);
                    var dataOnly = new byte[sigSectionStart];
                    Array.Copy(payloadToProcess, 0, dataOnly, 0, sigSectionStart);

                    try
                    {
                        using var rsa = new RSACryptoServiceProvider();
                        rsa.ImportParameters(rsaParams);
                        var ok = rsa.VerifyData(dataOnly, CryptoConfig.MapNameToOID("SHA256"), signature);
                        if (!ok)
                        {
                            //Net.Logger.LogWarning("ProcessIncomingFrame: Signature verification failed; dropping");
                            return;
                        }
                        payloadToProcess = dataOnly;
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame: Signature verification error: {ex}");
                        return;
                    }
                }

                if (compressed)
                {
                    try
                    {
                        payloadToProcess = Message.DecompressPayload(payloadToProcess, Message.MaxLogicalSize);
                    }
                    catch (Exception ex)
                    {
                        Net.Logger.LogError($"ProcessIncomingFrame: Decompression failed: {ex}");
                        return;
                    }
                }

                var message = new Message(payloadToProcess);

                if (message.ModID == 0)
                {
                    //Net.Logger.LogInfo($"ProcessIncomingFrame: internal message {message.MethodName}");
                    HandleInternalMessage(message, sender, msgId, seq, requiresAck);
                    if (requiresAck && message.MethodName != "NETWORK_INTERNAL_ACK")
                    {
                        //Net.Logger.LogInfo($"ProcessIncomingFrame: sending ACK for msgId={msgId} to {sender}");
                        SendAckToSender(sender, msgId);
                    }
                    return;
                }

                var sender64 = sender.m_SteamID;
                var rl = GetOrCreateRateLimiter(sender64);
                if (!rl.IncomingAllowed())
                {
                    //Net.Logger.LogWarning($"ProcessIncomingFrame: Rate limit: dropping incoming from {sender64}");
                    return;
                }

                if (!CheckAndUpdateSequence(sender64, message.ModID, seq))
                {
                    //Net.Logger.LogDebug($"ProcessIncomingFrame: Dropped replay/out-of-order seq {seq} from {sender64} for mod {message.ModID}");
                    return;
                }

                if (requiresAck)
                {
                    //Net.Logger.LogInfo($"ProcessIncomingFrame: will send ACK for msgId={msgId} to {sender}");
                    SendAckToSender(sender, msgId);
                }

                if (IncomingValidator != null && !IncomingValidator(message, sender64))
                {
                    //Net.Logger.LogDebug($"ProcessIncomingFrame: Incoming message from {sender64} rejected by validator");
                    return;
                }

                DispatchIncoming(message, sender);
            }
            catch (EndOfStreamException ex)
            {
                Net.Logger.LogWarning($"ProcessIncomingFrame: malformed frame from {sender}, dropping. {ex.Message}");
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"ProcessIncomingFrame top-level exception: {ex}");
            }
        }

        byte[] AppendFrameMac(byte[] frameWithoutTrailingMac, byte[] key)
        {
            using var h = new HMACSHA256(key);
            var mac = h.ComputeHash(frameWithoutTrailingMac);
            using var ms = new MemoryStream(frameWithoutTrailingMac.Length + mac.Length);
            ms.Write(frameWithoutTrailingMac, 0, frameWithoutTrailingMac.Length);
            ms.Write(mac, 0, mac.Length);
            return ms.ToArray();
        }

        bool VerifyAndStripSingleFrameMac(byte[] framedWithMac, byte[] key, out byte[] strippedFrame)
        {
            strippedFrame = Array.Empty<byte>();
            if (framedWithMac.Length < FRAME_HEADER_SIZE + 32) return false;
            int dataLen = framedWithMac.Length - 32;
            var frameScope = new byte[dataLen];
            Buffer.BlockCopy(framedWithMac, 0, frameScope, 0, dataLen);
            using var h = new HMACSHA256(key);
            var computed = h.ComputeHash(frameScope);
            var expectedMac = new byte[32];
            Buffer.BlockCopy(framedWithMac, dataLen, expectedMac, 0, expectedMac.Length);
            if (!CryptographicOperations.FixedTimeEquals(computed, expectedMac)) return false;
            strippedFrame = frameScope;
            return true;
        }

        bool TryVerifyAndStripFrameMacs(byte[] frameWithMacs, ulong senderSteamId, out byte[] verifiedFrame)
        {
            verifiedFrame = Array.Empty<byte>();

            byte[]? peerSym;
            byte[]? localGlobalSharedSecret;
            lock (cryptoStateLock)
            {
                peerSym = perPeerSymmetricKey.TryGetValue(senderSteamId, out var peer) ? (byte[])peer.Clone() : null;
                localGlobalSharedSecret = globalSharedSecret != null ? (byte[])globalSharedSecret.Clone() : null;
            }
            try
            {
                bool hasPeer = peerSym != null;
                bool hasGlobal = localGlobalSharedSecret != null;
                if (!hasPeer && !hasGlobal) return false;

                var current = frameWithMacs;
                if (hasPeer)
                {
                    if (!VerifyAndStripSingleFrameMac(current, peerSym!, out current)) return false;
                }

                if (hasGlobal)
                {
                    if (!VerifyAndStripSingleFrameMac(current, localGlobalSharedSecret!, out current)) return false;
                }

                verifiedFrame = current;
                return true;
            }
            finally
            {
                if (peerSym != null)
                {
                    CryptographicOperations.ZeroMemory(peerSym);
                }
                if (localGlobalSharedSecret != null)
                {
                    CryptographicOperations.ZeroMemory(localGlobalSharedSecret);
                }
            }
        }

        private static void ReadExact(Stream s, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = s.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException($"Expected {count} bytes but only received {offset}.");
                offset += read;
            }
        }

        private static ulong ReadU64(Stream s)
        {
            var b = new byte[8];
            ReadExact(s, b, b.Length);
            return BinaryPrimitives.ReadUInt64LittleEndian(b);
        }

        private static int ReadI32(Stream s)
        {
            var b = new byte[4];
            ReadExact(s, b, b.Length);
            return BinaryPrimitives.ReadInt32LittleEndian(b);
        }


        void SendAckToSender(CSteamID sender, ulong msgId)
        {
            //Net.Logger.LogInfo($"SendAckToSender: to={sender} ackId={msgId}");
            var ackMsg = new Message(0u, "NETWORK_INTERNAL_ACK", 0);
            ackMsg.WriteULong(msgId);
            // Transport ACK frames reliably so a dropped ACK does not stall sender-side retransmit logic.
            // We explicitly clear ACK_FLAG so ACK packets never request ACKs themselves.
            var framed = BuildFramedBytesWithMeta(ackMsg, 0, ReliableType.Reliable);
            if ((framed[0] & ACK_FLAG) != 0)
            {
                framed[0] = (byte)(framed[0] & ~ACK_FLAG);
            }
            SendBytes(framed, sender, ReliableType.Reliable);
        }

        void HandleInternalMessage(Message message, CSteamID sender, ulong msgId, ulong seq, bool requiresAck)
        {
            try
            {
                switch (message.MethodName)
                {
                    case "NETWORK_INTERNAL_HANDSHAKE_PUBKEY":
                        {
                            string pub = message.ReadString();
                            string nonce = message.ReadString();
                            StartHandshakeReply(sender, pub, nonce);
                            break;
                        }
                    case "NETWORK_INTERNAL_HANDSHAKE_SECRET":
                        {
                            var enc = (byte[])message.ReadObject(typeof(byte[]));
                            string initiator = message.ReadString();
                            CompleteHandshakeReceiver(sender, enc, initiator);
                            break;
                        }
                    case "NETWORK_INTERNAL_HANDSHAKE_CONFIRM":
                        {
                            string initiator = message.ReadString();
                            var confirm = (byte[])message.ReadObject(typeof(byte[]));
                            CompleteHandshakeInitiator(sender, initiator, confirm);
                            break;
                        }
                    case "NETWORK_INTERNAL_ACK":
                        {
                            ulong ackId = message.ReadULong();
                            var key = (sender.m_SteamID, ackId);
                            lock (unackedLock) { if (unacked.ContainsKey(key)) unacked.Remove(key); }
                            break;
                        }
                    default:
                        Net.Logger.LogWarning($"Unknown internal method {message.MethodName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"HandleInternalMessage error: {ex}");
            }
        }

        bool CheckAndUpdateSequence(ulong sender64, uint modId, ulong seq)
        {
            lock (lastSeenSequence)
            {
                if (!lastSeenSequence.TryGetValue(sender64, out var dict)) dict = lastSeenSequence[sender64] = new Dictionary<uint, ulong>();
                if (!dict.TryGetValue(modId, out var last)) last = 0;
                if (seq <= last) return false;
                dict[modId] = seq;
                return true;
            }
        }

        void DispatchIncoming(Message message, CSteamID sender)
        {
            MessageHandler[] handlersSnapshot;
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(message.ModID, out var methods))
                {
                    Net.Logger.LogWarning($"Dropping message for unknown mod {message.ModID}");
                    return;
                }
                if (!methods.TryGetValue(message.MethodName, out var handlers))
                {
                    Net.Logger.LogWarning($"Dropping message for method {message.MethodName} not registered for {message.ModID}");
                    return;
                }
                handlersSnapshot = handlers.ToArray();
            }

            IEnumerable<MessageHandler> candidates = handlersSnapshot.Where(h => h.Mask == message.Mask);
            if (!string.IsNullOrEmpty(message.OverloadKey))
            {
                var keyed = candidates.Where(h => BuildOverloadKey(h) == message.OverloadKey).ToArray();
                if (keyed.Length > 0) candidates = keyed.Concat(candidates.Where(h => BuildOverloadKey(h) != message.OverloadKey));
            }

            MessageHandler? chosenHandler = null;
            object[]? chosenParams = null;
            MessageHandler? fallbackHandler = null;
            object[]? fallbackParams = null;

            foreach (var handler in candidates)
            {
                if (!TryDeserializeForHandler(message, handler, sender, out var callParams, out int unread))
                    continue;

                if (unread == 0)
                {
                    chosenHandler = handler;
                    chosenParams = callParams;
                    break;
                }

                fallbackHandler ??= handler;
                fallbackParams ??= callParams;
            }

            if (chosenHandler == null)
            {
                chosenHandler = fallbackHandler;
                chosenParams = fallbackParams;
            }

            if (chosenHandler == null || chosenParams == null)
            {
                Net.Logger.LogWarning($"No handler matched for {message.ModID}:{message.MethodName} mask={message.Mask}");
                return;
            }

            try
            {
                chosenHandler.Method.Invoke(chosenHandler.Target, chosenParams);
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"Invoke RPC error: {ex}");
            }
        }

        object CreateRpcInfoInstance(Type infoType, CSteamID sender)
        {
            try
            {
                var ctorFull = infoType.GetConstructor(new Type[] { typeof(ulong), typeof(string), typeof(bool) });
                if (ctorFull != null)
                {
                    bool isLocal = false;
                    try { isLocal = (sender == SteamUser.GetSteamID()); } catch { isLocal = false; }
                    return ctorFull.Invoke(new object[] { sender.m_SteamID, sender.ToString(), isLocal });
                }

                var ci = infoType.GetConstructor(new Type[] { typeof(CSteamID) });
                if (ci != null) return ci.Invoke(new object[] { sender });

                var ci2 = infoType.GetConstructor(new Type[] { typeof(ulong) });
                if (ci2 != null) return ci2.Invoke(new object[] { sender.m_SteamID });

                var paramless = infoType.GetConstructor(Type.EmptyTypes);
                if (paramless != null)
                {
                    var obj = paramless.Invoke(null);
                    AssignRpcIdentityMembers(obj, infoType, sender);
                    return obj;
                }
            }
            catch { }
            return null!;
        }

        static void AssignRpcIdentityMembers(object instance, Type infoType, CSteamID sender)
        {
            var steamId64 = sender.m_SteamID;
            var steamIdString = sender.ToString();

            AssignRpcIdentityMember(instance, infoType, "SenderSteamID", sender, steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "Sender", sender, steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamId64", sender, steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamIdString", sender, steamId64, steamIdString);
        }

        static void AssignRpcIdentityMember(object instance, Type infoType, string memberName, CSteamID sender, ulong steamId64, string steamIdString)
        {
            var field = infoType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                TryAssignMemberValue(field.FieldType, value => field.SetValue(instance, value), sender, steamId64, steamIdString);
                return;
            }

            var property = infoType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite) return;
            TryAssignMemberValue(property.PropertyType, value => property.SetValue(instance, value), sender, steamId64, steamIdString);
        }

        static void TryAssignMemberValue(Type memberType, Action<object> assign, CSteamID sender, ulong steamId64, string steamIdString)
        {
            var t = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (t == typeof(CSteamID))
            {
                assign(sender);
                return;
            }
            if (t == typeof(string))
            {
                assign(steamIdString);
                return;
            }
            if (!IsCompatibleIntegralType(t)) return;
            assign(ConvertIntegral(steamId64, t));
        }

        static bool IsCompatibleIntegralType(Type t)
        {
            return t == typeof(ulong)
                || t == typeof(long)
                || t == typeof(uint)
                || t == typeof(int)
                || t == typeof(ushort)
                || t == typeof(short)
                || t == typeof(byte)
                || t == typeof(sbyte);
        }

        static object ConvertIntegral(ulong value, Type targetType)
        {
            if (targetType == typeof(ulong)) return value;
            checked
            {
                if (targetType == typeof(long)) return (long)value;
                if (targetType == typeof(uint)) return (uint)value;
                if (targetType == typeof(int)) return (int)value;
                if (targetType == typeof(ushort)) return (ushort)value;
                if (targetType == typeof(short)) return (short)value;
                if (targetType == typeof(byte)) return (byte)value;
                if (targetType == typeof(sbyte)) return (sbyte)value;
            }
            throw new InvalidCastException($"Unsupported integral conversion to {targetType}.");
        }

        class HandshakeState { public string? PeerPub; public string LocalNonce = string.Empty; public byte[]? Sym; public bool Completed = false; }

        /// <summary>
        /// </summary>
        public void StartHandshake(CSteamID target)
        {
            if (LocalRsa == null) return;
            var pub = SerializeRsaPublicKey(LocalRsa);
            using var rng = RandomNumberGenerator.Create();
            var nonceBytes = new byte[16]; rng.GetBytes(nonceBytes);
            var nonce = Convert.ToBase64String(nonceBytes);

            lock (cryptoStateLock)
            {
                handshakeStates[target.m_SteamID] = new HandshakeState { PeerPub = null, LocalNonce = nonce, Completed = false };
            }

            var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0);
            m.WriteString(pub);
            m.WriteString(nonce);
            var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
            SendBytes(framed, target, ReliableType.Reliable);
        }

        void StartHandshakeReply(CSteamID sender, string peerPubKeySerialized, string peerNonce)
        {
            if (LocalRsa == null) return;
            using var rng = RandomNumberGenerator.Create();
            var localNonceBytes = new byte[16];
            rng.GetBytes(localNonceBytes);
            var generatedLocalNonce = Convert.ToBase64String(localNonceBytes);

            string? localNonceToSend = null;
            string? peerPubForSecret = null;
            string? localNonceForSecret = null;
            lock (cryptoStateLock)
            {
                var state = handshakeStates.ContainsKey(sender.m_SteamID) ? handshakeStates[sender.m_SteamID] : new HandshakeState();
                state.PeerPub = peerPubKeySerialized;
                if (string.IsNullOrEmpty(state.LocalNonce))
                {
                    state.LocalNonce = generatedLocalNonce;
                    handshakeStates[sender.m_SteamID] = state;
                    localNonceToSend = state.LocalNonce;
                }
                else if (!state.Completed && !string.IsNullOrEmpty(state.PeerPub))
                {
                    peerPubForSecret = state.PeerPub;
                    localNonceForSecret = state.LocalNonce;
                }
            }

            if (!string.IsNullOrEmpty(localNonceToSend))
            {
                var myPub = SerializeRsaPublicKey(LocalRsa);
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0);
                m.WriteString(myPub);
                m.WriteString(localNonceToSend);
                var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
                SendBytes(framed, sender, ReliableType.Reliable);
                return;
            }

            if (string.IsNullOrEmpty(peerPubForSecret) || string.IsNullOrEmpty(localNonceForSecret)) return;

            var sym = new byte[32];
            rng.GetBytes(sym);

            byte[] enc;
            try
            {
                using var rsaPeer = new RSACryptoServiceProvider();
                var rsaParams = DeserializeRsaPublicKey(peerPubForSecret);
                rsaPeer.ImportParameters(rsaParams);
                enc = rsaPeer.Encrypt(sym, false);
            }
            catch (Exception ex)
            {
                Net.Logger.LogWarning($"Handshake reply rejected invalid peer RSA key from {sender}: {ex.Message}");
                CryptographicOperations.ZeroMemory(sym);
                return;
            }

            var m2 = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_SECRET", 0);
            m2.WriteBytes(enc);
            m2.WriteString(localNonceForSecret);
            var framed2 = BuildFramedBytesWithMeta(m2, 0, ReliableType.Reliable);
            SendBytes(framed2, sender, ReliableType.Reliable);

            lock (cryptoStateLock)
            {
                var state = handshakeStates.ContainsKey(sender.m_SteamID) ? handshakeStates[sender.m_SteamID] : new HandshakeState();
                state.Sym = sym;
                state.Completed = true;
                handshakeStates[sender.m_SteamID] = state;
                perPeerSymmetricKey[sender.m_SteamID] = sym;
            }
        }

        void CompleteHandshakeReceiver(CSteamID sender, byte[] encSecret, string initiatorNonce)
        {
            if (LocalRsa == null) return;
            try
            {
                var sym = LocalRsa.Decrypt(encSecret, false);
                lock (cryptoStateLock)
                {
                    var state = handshakeStates.ContainsKey(sender.m_SteamID) ? handshakeStates[sender.m_SteamID] : new HandshakeState();
                    state.Sym = sym;
                    state.Completed = true;
                    handshakeStates[sender.m_SteamID] = state;
                    perPeerSymmetricKey[sender.m_SteamID] = sym;
                }

                var confirm = HmacSha256Raw(sym, Encoding.UTF8.GetBytes(initiatorNonce));
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_CONFIRM", 0);
                m.WriteString(initiatorNonce);
                m.WriteBytes(confirm);
                var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
                SendBytes(framed, sender, ReliableType.Reliable);
            }
            catch (Exception ex) { Net.Logger.LogError($"Handshake decryption error: {ex}"); }
        }

        void CompleteHandshakeInitiator(CSteamID sender, string initiatorNonce, byte[] confirmHmac)
        {
            HandshakeState? state;
            byte[]? sym;
            lock (cryptoStateLock)
            {
                handshakeStates.TryGetValue(sender.m_SteamID, out state);
                sym = state?.Sym;
            }
            if (state == null || sym == null)
            {
                Net.Logger.LogWarning("Handshake confirm received but no state");
                return;
            }

            var expected = HmacSha256Raw(sym, Encoding.UTF8.GetBytes(initiatorNonce));
            if (!expected.SequenceEqual(confirmHmac))
            {
                Net.Logger.LogWarning("Handshake confirm HMAC mismatch");
                return;
            }

            lock (cryptoStateLock)
            {
                if (!handshakeStates.TryGetValue(sender.m_SteamID, out var current) || current.Sym == null) return;
                if (!current.Sym.SequenceEqual(sym))
                {
                    Net.Logger.LogWarning("Handshake state changed before confirm commit; discarding stale confirmation");
                    return;
                }
                current.Completed = true;
                handshakeStates[sender.m_SteamID] = current;
                perPeerSymmetricKey[sender.m_SteamID] = sym;
            }
        }

        static byte[] HmacSha256Raw(byte[] key, byte[] payload)
        {
            using var h = new HMACSHA256(key);
            return h.ComputeHash(payload);
        }

        static string SerializeRsaPublicKey(RSACryptoServiceProvider rsa)
        {
            var parms = rsa.ExportParameters(false);
            var mod = Convert.ToBase64String(parms.Modulus ?? Array.Empty<byte>());
            var exp = Convert.ToBase64String(parms.Exponent ?? Array.Empty<byte>());
            return $"{mod}:{exp}";
        }

        static RSAParameters DeserializeRsaPublicKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new FormatException("Serialized RSA key is empty.");

            var parts = s.Split(':');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                throw new FormatException("Serialized RSA key must contain modulus and exponent segments.");

            var mod = Convert.FromBase64String(parts[0]);
            var exp = Convert.FromBase64String(parts[1]);
            if (mod.Length == 0 || exp.Length == 0)
                throw new FormatException("Serialized RSA key modulus and exponent must not be empty.");
            return new RSAParameters { Modulus = mod, Exponent = exp };
        }

        /// <summary>
        /// </summary>
        public void SetSharedSecret(byte[]? secret)
        {
            lock (cryptoStateLock)
            {
                ClearGlobalSharedSecretUnderLock();
                if (secret == null)
                {
                    globalHmac?.Dispose();
                    globalHmac = null;
                    return;
                }
                globalSharedSecret = (byte[])secret.Clone();
                globalHmac?.Dispose();
                globalHmac = new HMACSHA256(globalSharedSecret);
            }
        }

        /// <summary>
        /// </summary>
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate)
        {
            ArgumentNullException.ThrowIfNull(signerDelegate);
            lock (cryptoStateLock)
            {
                modSigners[modId] = signerDelegate;
            }
        }
        /// <summary>
        /// </summary>
        public void RegisterModPublicKey(uint modId, RSAParameters pub)
        {
            if (pub.Modulus == null || pub.Modulus.Length == 0)
                throw new ArgumentException("RSA public key modulus must not be empty.", nameof(pub));
            if (pub.Exponent == null || pub.Exponent.Length == 0)
                throw new ArgumentException("RSA public key exponent must not be empty.", nameof(pub));
            lock (cryptoStateLock)
            {
                modPublicKeys[modId] = pub;
            }
        }

        void InvokeLocalMessage(Message message, CSteamID localSender)
        {
            ulong id = localSender.m_SteamID;
            if (IncomingValidator != null && !IncomingValidator(message, id)) return;

            MessageHandler[] handlersSnapshot;
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(message.ModID, out var methods)) return;
                if (!methods.TryGetValue(message.MethodName, out var handlers)) return;
                handlersSnapshot = handlers.ToArray();
            }

            IEnumerable<MessageHandler> candidates = handlersSnapshot.Where(h => h.Mask == message.Mask);
            if (!string.IsNullOrEmpty(message.OverloadKey))
            {
                var keyed = candidates.Where(h => BuildOverloadKey(h) == message.OverloadKey).ToArray();
                if (keyed.Length > 0) candidates = keyed.Concat(candidates.Where(h => BuildOverloadKey(h) != message.OverloadKey));
            }

            var deserialized = new List<(MessageHandler Handler, object[] CallParams, int Unread, string OverloadKey)>();
            foreach (var handler in candidates)
            {
                if (!TryDeserializeForHandler(message, handler, localSender, out var callParams, out int unread))
                    continue;

                deserialized.Add((handler, callParams, unread, BuildOverloadKey(handler)));
            }

            if (deserialized.Count == 0)
            {
                Net.Logger.LogWarning($"No handler matched for local {message.ModID}:{message.MethodName} mask={message.Mask}");
                return;
            }

            int chosenIndex = deserialized.FindIndex(c => c.Unread == 0);
            if (chosenIndex < 0) chosenIndex = 0;
            string chosenOverloadKey = deserialized[chosenIndex].OverloadKey;

            bool invokedAny = false;
            foreach (var candidate in deserialized.Where(c => c.OverloadKey == chosenOverloadKey))
            {
                try
                {
                    candidate.Handler.Method.Invoke(candidate.Handler.Target, candidate.CallParams);
                    invokedAny = true;
                }
                catch (Exception ex)
                {
                    Net.Logger.LogError($"Local invoke error: {ex}");
                }
            }

            if (!invokedAny)
                Net.Logger.LogWarning($"No handler matched for local {message.ModID}:{message.MethodName} mask={message.Mask}");
        }

        bool TryDeserializeForHandler(Message source, MessageHandler handler, CSteamID sender, out object[] callParams, out int unread)
        {
            callParams = null!;
            unread = int.MaxValue;
            try
            {
                var msgCopy = new Message(source.ToArray());
                var paramInfos = handler.Parameters;
                int paramCount = handler.TakesInfo ? paramInfos.Length - 1 : paramInfos.Length;
                callParams = new object[paramInfos.Length];

                for (int i = 0; i < paramCount; i++) callParams[i] = msgCopy.ReadObject(paramInfos[i].ParameterType);
                if (handler.TakesInfo)
                {
                    var infoType = paramInfos[paramInfos.Length - 1].ParameterType;
                    callParams[paramInfos.Length - 1] = CreateRpcInfoInstance(infoType, sender);
                }

                unread = msgCopy.UnreadLength();
                return true;
            }
            catch
            {
                return false;
            }
        }

        Message? BuildMessage(uint modId, string methodName, int mask, object?[] parameters, Type[]? parameterTypes)
        {
            try
            {
                var msg = new Message(modId, methodName, mask);
                MessageHandler[] handlersSnapshot = Array.Empty<MessageHandler>();
                lock (rpcLock)
                {
                    if (rpcs.TryGetValue(modId, out var methods) && methods.TryGetValue(methodName, out var handlers) && handlers.Count > 0)
                        handlersSnapshot = handlers.ToArray();
                }

                if (handlersSnapshot.Length > 0)
                {
                    MessageHandler chosen = null!;
                    foreach (var h in handlersSnapshot)
                    {
                        if (h.Mask != mask) continue;
                        var expected = h.Parameters;
                        int expectedCount = h.TakesInfo ? expected.Length - 1 : expected.Length;
                        if (expectedCount != parameters.Length) continue;

                        bool ok = true;
                        for (int i = 0; i < expectedCount; i++)
                        {
                            var t = expected[i].ParameterType;
                            var p = parameters[i];
                            if (p == null)
                            {
                                if (t.IsValueType && Nullable.GetUnderlyingType(t) == null) { ok = false; break; }
                                continue;
                            }
                            if (!t.IsAssignableFrom(p.GetType())) { ok = false; break; }
                        }
                        if (ok) { chosen = h; break; }
                    }

                    if (chosen == null)
                    {
                        chosen = handlersSnapshot.FirstOrDefault(h =>
                        {
                            int expectedCount = h.TakesInfo ? h.Parameters.Length - 1 : h.Parameters.Length;
                            return expectedCount == parameters.Length && h.Mask == mask;
                        });
                    }

                    if (chosen == null)
                    {
                        Net.Logger.LogError($"No RPC overload matched method '{methodName}' for mask {mask} and parameter list.");
                        return null;
                    }

                    msg = new Message(modId, methodName, mask, BuildOverloadKey(chosen));
                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.TakesInfo ? expectedParams.Length - 1 : expectedParams.Length;
                    for (int i = 0; i < expectedCountFinal; i++)
                    {
                        var t = expectedParams[i].ParameterType;
                        var p = parameters[i];
                        if (p == null)
                        {
                            if (t.IsValueType && Nullable.GetUnderlyingType(t) == null)
                                throw new Exception($"Parameter {i} for {methodName} cannot be null; expected non-nullable {t}.");
                            msg.WriteObject(t, null!);
                            continue;
                        }
                        if (!t.IsAssignableFrom(p.GetType()))
                            throw new Exception($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}");
                        msg.WriteObject(t, p);
                    }
                }
                else
                {
                    if (parameterTypes != null)
                    {
                        if (parameterTypes.Length != parameters.Length)
                            throw new Exception($"Parameter type count mismatch: expected {parameterTypes.Length}, got {parameters.Length}");
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            var t = parameterTypes[i];
                            var p = parameters[i];
                            if (p == null)
                            {
                                if (t.IsValueType && Nullable.GetUnderlyingType(t) == null)
                                    throw new Exception($"Parameter {i} for {methodName} cannot be null; expected non-nullable {t}.");
                                msg.WriteObject(t, null!);
                                continue;
                            }
                            if (!t.IsAssignableFrom(p.GetType()))
                                throw new Exception($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}");
                            msg.WriteObject(t, p);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            var p = parameters[i] ?? throw new Exception($"Parameter {i} is null for unregistered RPC {methodName}; use typed RPC overload.");
                            msg.WriteObject(p.GetType(), p);
                        }
                    }
                }

                if (msg.Length() > Message.MaxLogicalSize)
                {
                    Net.Logger.LogError("Message exceeds maximum allowed overall size.");
                    return null;
                }

                return msg;
            }
            catch (Exception ex)
            {
                Net.Logger.LogError($"BuildMessage failed: {ex}");
                return null;
            }
        }

        static bool IsRpcInfoParameterType(Type parameterType)
        {
            if (parameterType == typeof(RPCInfo)) return true;
            var fullName = parameterType.FullName;
            return fullName != null && CompatibleRpcInfoTypeNames.Contains(fullName);
        }

        static string BuildOverloadKey(MessageHandler handler)
        {
            var pi = handler.Parameters;
            int parameterCount = handler.TakesInfo ? pi.Length - 1 : pi.Length;
            if (parameterCount <= 0) return string.Empty;
            return string.Join("|", pi.Take(parameterCount).Select(p => p.ParameterType.AssemblyQualifiedName ?? p.ParameterType.FullName ?? p.ParameterType.Name));
        }

        SlidingWindowRateLimiter GetOrCreateRateLimiter(ulong steam64)
        {
            lock (rateLimiters)
            {
                if (!rateLimiters.TryGetValue(steam64, out var rl))
                {
                    rl = new SlidingWindowRateLimiter(100, TimeSpan.FromSeconds(1));
                    rateLimiters[steam64] = rl;
                }
                return rl;
            }
        }

        class SlidingWindowRateLimiter
        {
            readonly int limit;
            readonly TimeSpan window;
            readonly Queue<DateTime> q = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool Allowed()
            {
                var now = DateTime.UtcNow;
                while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                if (q.Count >= limit) return false;
                q.Enqueue(now);
                return true;
            }
            public bool IncomingAllowed() => Allowed();
        }

        enum Priority { High = 0, Normal = 1, Low = 2 }
        class QueuedSend { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime Enqueued; }
        class UnackedMessage { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime LastSent; public int Attempts; public ulong msgId => BinaryPrimitives.ReadUInt64LittleEndian(Framed.AsSpan(1, 8)); }
        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; }

        static byte[] HmacSha256RawStatic(byte[] key, byte[] payload) { using var h = new HMACSHA256(key); return h.ComputeHash(payload); }
    }
}
#else
using NetworkingLibrary.Modules;
using System;
using System.Security.Cryptography;

namespace NetworkingLibrary.Services
{
    /// <summary>
    /// Editor-safe fallback that preserves the SteamNetworkingService type without Steamworks dependencies.
    /// </summary>
    public class SteamNetworkingService : INetworkingService
    {
        readonly OfflineNetworkingService offline = new();

        public bool IsInitialized => offline.IsInitialized;
        public bool InLobby => offline.InLobby;
        public ulong HostSteamId64 => offline.HostSteamId64;
        public string HostIdString => offline.HostIdString;
        public bool IsHost => offline.IsHost;
        public Func<Message, ulong, bool>? IncomingValidator
        {
            get => offline.IncomingValidator;
            set => offline.IncomingValidator = value;
        }

        public event Action? LobbyCreated
        {
            add => offline.LobbyCreated += value;
            remove => offline.LobbyCreated -= value;
        }

        public event Action? LobbyEntered
        {
            add => offline.LobbyEntered += value;
            remove => offline.LobbyEntered -= value;
        }

        public event Action? LobbyLeft
        {
            add => offline.LobbyLeft += value;
            remove => offline.LobbyLeft -= value;
        }

        public event Action<ulong>? PlayerEntered
        {
            add => offline.PlayerEntered += value;
            remove => offline.PlayerEntered -= value;
        }

        public event Action<ulong>? PlayerLeft
        {
            add => offline.PlayerLeft += value;
            remove => offline.PlayerLeft -= value;
        }

        public event Action<string[]>? LobbyDataChanged
        {
            add => offline.LobbyDataChanged += value;
            remove => offline.LobbyDataChanged -= value;
        }

        public event Action<ulong, string[]>? PlayerDataChanged
        {
            add => offline.PlayerDataChanged += value;
            remove => offline.PlayerDataChanged -= value;
        }

        public ulong GetLocalSteam64() => offline.GetLocalSteam64();
        public ulong[] GetLobbyMemberSteamIds() => offline.GetLobbyMemberSteamIds();
        public void Initialize() => offline.Initialize();
        public void Shutdown() => offline.Shutdown();
        public void CreateLobby(int maxPlayers = 8) => offline.CreateLobby(maxPlayers);
        public void JoinLobby(ulong lobbySteamId64) => offline.JoinLobby(lobbySteamId64);
        public void LeaveLobby() => offline.LeaveLobby();
        public void InviteToLobby(ulong steamId64) => offline.InviteToLobby(steamId64);
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0) => offline.RegisterNetworkObject(instance, modId, mask);
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0) => offline.RegisterNetworkType(type, modId, mask);
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0) => offline.DeregisterNetworkObject(instance, modId, mask);
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0) => offline.DeregisterNetworkType(type, modId, mask);
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters) => offline.RPC(modId, methodName, reliable, parameters);
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) => offline.RPCTarget(modId, methodName, targetSteamId64, reliable, parameters);
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters) => offline.RPCToHost(modId, methodName, reliable, parameters);
        public void RegisterLobbyDataKey(string key) => offline.RegisterLobbyDataKey(key);
        public void SetLobbyData(string key, object value) => offline.SetLobbyData(key, value);
        public T GetLobbyData<T>(string key) => offline.GetLobbyData<T>(key);
        public void RegisterPlayerDataKey(string key) => offline.RegisterPlayerDataKey(key);
        public void SetPlayerData(string key, object value) => offline.SetPlayerData(key, value);
        public T GetPlayerData<T>(ulong steamId64, string key) => offline.GetPlayerData<T>(steamId64, key);
        public void PollReceive() => offline.PollReceive();
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) => offline.RegisterModSigner(modId, signerDelegate);
        public void RegisterModPublicKey(uint modId, RSAParameters pub) => offline.RegisterModPublicKey(modId, pub);
    }
}
#endif
