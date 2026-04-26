#if !UNITY_EDITOR
using NetworkingLibrary.Modules;
using pworld.Scripts;
using Steamworks;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
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
    public class SteamNetworkingService : INetworkingService
    {
        const string LogSource = "SteamNetworkingService";
        const int CHANNEL = 120;
        const int MAX_IN_MESSAGES = 500;
        const int MAX_NORMAL_QUEUE_DEPTH = 256;
        const int MAX_LOW_QUEUE_DEPTH = 128;
        const double DebugLogCooldownSeconds = 2d;
        const double QueueOverflowWarningCooldownSeconds = 2d;
        const string GetLocalSteam64DebugCooldownKey = "SteamNetworkingService.GetLocalSteam64";
        const string IsHostDebugCooldownKey = "SteamNetworkingService.IsHost";
        readonly IntPtr[] inMessages = new IntPtr[MAX_IN_MESSAGES];
        private readonly object rpcLock = new object();
        private readonly object cryptoStateLock = new();

        public bool IsInitialized { get; private set; } = false;
        public bool InLobby { get; private set; } = false;
        public ulong HostSteamId64
        {
            get
            {
                if (Lobby == CSteamID.Nil) return 0UL;
                var owner = getLobbyOwner(Lobby);
                if (owner == CSteamID.Nil) return 0UL;
                return owner.m_SteamID;
            }
        }
        public string HostIdString
        {
            get
            {
                if (Lobby == CSteamID.Nil) return string.Empty;
                var owner = getLobbyOwner(Lobby);
                return owner == CSteamID.Nil ? string.Empty : owner.ToString();
            }
        }

        public ulong GetLocalSteam64()
        {
            try
            {
                return getLocalSteamId().m_SteamID;
            }
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, GetLocalSteam64DebugCooldownKey, DebugLogCooldownSeconds, $"GetLocalSteam64 failed: {ex.GetType().Name}: {ex.Message}");
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
                NetLog.Error(LogSource, $"GetLobbyMemberSteamIds error: {ex}");
                return Array.Empty<ulong>();
            }
        }


        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        private CSteamID[] players = Array.Empty<CSteamID>();

        public bool IsHost
        {
            get
            {
                try
                {
                    if (!InLobby) return false;
                    var owner = getLobbyOwner(Lobby);
                    if (owner == CSteamID.Nil) return false;
                    return owner == getLocalSteamId();
                }
                catch (Exception ex)
                {
                    NetLog.DebugThrottled(LogSource, IsHostDebugCooldownKey, DebugLogCooldownSeconds, $"IsHost check failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
        }
        public event Action? LobbyCreated;
        public event Action? LobbyEntered;
        public event Action? LobbyLeft;
        public event Action<ulong>? PlayerEntered;
        public event Action<ulong>? PlayerLeft;
        public event Action<string[]>? LobbyDataChanged;
        public event Action<ulong, string[]>? PlayerDataChanged;

        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        private readonly List<string> lobbyDataKeys = new();
        private readonly List<string> playerDataKeys = new();
        private readonly Dictionary<CSteamID, Dictionary<string, string>> lastPlayerData = new();
        private readonly Dictionary<string, string> lastLobbyData = new();
        private Func<CSteamID, int> getNumLobbyMembers = SteamMatchmaking.GetNumLobbyMembers;
        private Func<CSteamID, CSteamID> getLobbyOwner = SteamMatchmaking.GetLobbyOwner;
        private Func<CSteamID> getLocalSteamId = SteamUser.GetSteamID;
        private Func<CSteamID, int, CSteamID> getLobbyMemberByIndex = SteamMatchmaking.GetLobbyMemberByIndex;
        private Action<CSteamID, string, string> setLobbyData = SteamMatchmaking.SetLobbyData;
        private Func<CSteamID, string, string> getLobbyData = SteamMatchmaking.GetLobbyData;
        private Action<CSteamID, string, string> setLobbyMemberData = SteamMatchmaking.SetLobbyMemberData;
        private Func<CSteamID, CSteamID, string, string> getLobbyMemberData = SteamMatchmaking.GetLobbyMemberData;

        Callback<LobbyEnter_t>? cbLobbyEnter;
        Callback<LobbyCreated_t>? cbLobbyCreated;
        Callback<LobbyChatUpdate_t>? cbLobbyChatUpdate;
        Callback<LobbyDataUpdate_t>? cbLobbyDataUpdate;

        readonly Dictionary<uint, Dictionary<string, List<MessageHandler>>> rpcs = new();

        readonly Queue<QueuedSend> normalQueue = new();
        readonly Queue<QueuedSend> lowQueue = new();
        readonly object queueLock = new();

        readonly Dictionary<(ulong target, ulong msgId), UnackedMessage> unacked = new();
        readonly object unackedLock = new();
        readonly List<(ulong sender, ulong msgId)> staleFragmentKeys = new();
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
        RSAParameters[] modPublicKeysSnapshot = Array.Empty<RSAParameters>();

        readonly Dictionary<ulong, HandshakeState> handshakeStates = new();

        const byte FRAG_FLAG = 0x1;
        const byte COMPRESSED_FLAG = 0x2;
        const byte HMAC_FLAG = 0x4;
        const byte SIGN_FLAG = 0x8;
        const byte ACK_FLAG = 0x10;
        const int FRAME_HEADER_SIZE = 25;

        RSA? LocalRsa;
        private Func<RSA> localRsaFactory = () => RSA.Create(2048);
        private readonly MessageSizePolicy messageSizePolicy;

        public SteamNetworkingService(MessageSizePolicy? messageSizePolicy = null)
        {
            this.messageSizePolicy = messageSizePolicy ?? CreateDefaultMessageSizePolicy();
        }

        static MessageSizePolicy CreateDefaultMessageSizePolicy()
        {
            try
            {
                return new MessageSizePolicy((int)Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend);
            }
            catch
            {
                return Message.DefaultSizePolicy;
            }
        }

        readonly Dictionary<(ulong sender, ulong msgId), FragmentBuffer> fragmentBuffers = new();
        readonly object fragmentLock = new();
        readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(30);
        readonly TimeSpan FragmentCleanupInterval = TimeSpan.FromSeconds(5);
        DateTime nextFragmentCleanupAt = DateTime.MinValue;

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

        public void Initialize()
        {
            if (IsInitialized) return;

            GameObject? createdPumpGameObject = null;
            SteamCallbackPump? createdPumpComponent = null;
            var pumpingWasEnabledBeforeInitialize = SteamCallbackPump.CallbackPumpingEnabled;
            var enabledPumpingInThisInitialize = false;
            var pumpSetupSucceeded = !Application.isPlaying;
            if (Application.isPlaying)
            {
                try
                {
                    var existingPumps = UnityEngine.Object.FindObjectsOfType<SteamCallbackPump>(true);
                    var existingPump = existingPumps?.FirstOrDefault(p => p != null && p.isActiveAndEnabled);
                    var go = existingPump != null ? existingPump.gameObject : GameObject.Find("SteamCallbackPump");
                    if (go == null)
                    {
                        go = new GameObject("SteamCallbackPump");
                        createdPumpGameObject = go;
                        GameObject.DontDestroyOnLoad(go);
                        createdPumpComponent = go.AddComponent<SteamCallbackPump>();
                        NetLog.Info(LogSource, "Created SteamCallbackPump GameObject.");
                    }
                    else
                    {
                        if (!go.activeSelf) go.SetActive(true);
                        var pump = go.GetComponent<SteamCallbackPump>();
                        if (pump == null)
                        {
                            createdPumpComponent = go.AddComponent<SteamCallbackPump>();
                        }
                        else if (!pump.enabled)
                        {
                            pump.enabled = true;
                        }
                    }
                    GameObject.DontDestroyOnLoad(go);

                    SteamCallbackPump.EnablePumping();
                    enabledPumpingInThisInitialize = true;
                    pumpSetupSucceeded = true;
                }
                catch (Exception ex)
                {
                    NetLog.Error(LogSource, $"Failed to create SteamCallbackPump: {ex}");
                }
            }

            if (!pumpSetupSucceeded)
            {
                CleanupFailedPumpSetup(enabledPumpingInThisInitialize, pumpingWasEnabledBeforeInitialize, createdPumpGameObject, createdPumpComponent, "Initialize.DisablePumpingAfterPumpSetupFailure");
                IsInitialized = false;
                return;
            }

            if (!TryInitializeSteamCallbacksAndCrypto())
            {
                CleanupFailedPumpSetup(enabledPumpingInThisInitialize, pumpingWasEnabledBeforeInitialize, createdPumpGameObject, createdPumpComponent, "Initialize.DisablePumpingAfterCallbackFailure");
                IsInitialized = false;
                return;
            }

            IsInitialized = true;
            NetLog.Info(LogSource, "SteamNetworkingService initialized");
        }

        private void CleanupFailedPumpSetup(bool enabledPumpingInThisInitialize, bool pumpingWasEnabledBeforeInitialize, GameObject? createdPumpGameObject, SteamCallbackPump? createdPumpComponent, string disablePumpingContextKey)
        {
            if (enabledPumpingInThisInitialize && !pumpingWasEnabledBeforeInitialize)
            {
                try { SteamCallbackPump.DisablePumping(); }
                catch (Exception ex)
                {
                    var disablePumpingMessage = disablePumpingContextKey == "Initialize.DisablePumpingAfterPumpSetupFailure"
                        ? "Initialize failed pump setup cleanup at SteamCallbackPump.DisablePumping"
                        : disablePumpingContextKey == "Initialize.DisablePumpingAfterCallbackFailure"
                            ? "Initialize failed callback/crypto cleanup at SteamCallbackPump.DisablePumping"
                            : "Initialize failed cleanup at SteamCallbackPump.DisablePumping";
                    NetLog.DebugThrottled(LogSource, disablePumpingContextKey, DebugLogCooldownSeconds, $"{disablePumpingMessage}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            try
            {
                if (createdPumpGameObject != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(createdPumpGameObject);
                    else UnityEngine.Object.DestroyImmediate(createdPumpGameObject);
                }
                else if (createdPumpComponent != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(createdPumpComponent);
                    else UnityEngine.Object.DestroyImmediate(createdPumpComponent);
                }
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"Failed to cleanup created SteamCallbackPump after initialization failure: {ex}");
            }
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
                NetLog.Error(LogSource, $"Failed to initialize Steam callbacks and crypto: {ex}");
                cbLobbyEnter = null;
                cbLobbyCreated = null;
                cbLobbyChatUpdate = null;
                cbLobbyDataUpdate = null;
                LocalRsa = null;
                return false;
            }
        }

        public void Shutdown()
        {
            if (InLobby || Lobby != CSteamID.Nil)
                LeaveLobby();

            cbLobbyEnter = null;
            cbLobbyCreated = null;
            cbLobbyChatUpdate = null;
            cbLobbyDataUpdate = null;
            lock (rpcLock)
            {
                rpcs.Clear();
            }
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
                modSigners.Clear();
                modPublicKeys.Clear();
                modPublicKeysSnapshot = Array.Empty<RSAParameters>();
                handshakeStates.Clear();
                ClearPerPeerSymmetricKeysUnderLock();
                ClearGlobalSharedSecretUnderLock();
                globalHmac?.Dispose();
                globalHmac = null;
            }
            LocalRsa?.Dispose();
            LocalRsa = null;

            IsInitialized = false;
            try { SteamCallbackPump.DisableAndDestroyExisting(); }
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "Shutdown.DisableAndDestroyExisting", DebugLogCooldownSeconds, $"Shutdown cleanup at SteamCallbackPump.DisableAndDestroyExisting failed: {ex.GetType().Name}: {ex.Message}");
            }
            lock (lastSeenSequence) lastSeenSequence.Clear();
            lock (rateLimiters) rateLimiters.Clear();
            lock (outgoingSequencePerMod) outgoingSequencePerMod.Clear();
            lock (fragmentLock)
            {
                fragmentBuffers.Clear();
                staleFragmentKeys.Clear();
                nextFragmentCleanupAt = DateTime.MinValue;
            }
            NetLog.Info(LogSource, "SteamNetworkingService shutdown");
        }

        public void CreateLobby(int maxPlayers = 8)
        {
            if (!IsInitialized)
            {
                NetLog.Error(LogSource, "CreateLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (maxPlayers <= 0)
            {
                NetLog.Warning(LogSource, $"CreateLobby maxPlayers {maxPlayers} is invalid. Clamping to 1.");
                maxPlayers = 1;
            }

            try { SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxPlayers); }
            catch (Exception ex) { NetLog.Error(LogSource, $"CreateLobby failed: {ex}"); }
        }
        public void JoinLobby(ulong lobbySteamId64)
        {
            if (!IsInitialized)
            {
                NetLog.Error(LogSource, "JoinLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (lobbySteamId64 == 0UL)
            {
                NetLog.Warning(LogSource, "JoinLobby called with invalid lobby id 0.");
                return;
            }

            try { SteamMatchmaking.JoinLobby(new CSteamID(lobbySteamId64)); }
            catch (Exception ex) { NetLog.Error(LogSource, $"JoinLobby failed for lobby {lobbySteamId64}: {ex}"); }
        }
        public void LeaveLobby()
        {
            if (!InLobby || Lobby == CSteamID.Nil) return;

            try
            {
                SteamMatchmaking.LeaveLobby(Lobby);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"LeaveLobby failed for lobby {Lobby}: {ex}");
            }

            OnLobbyLeftInternal();
        }
        public void InviteToLobby(ulong steamId64)
        {
            if (!IsInitialized)
            {
                NetLog.Error(LogSource, "InviteToLobby called before SteamNetworkingService.Initialize.");
                return;
            }

            if (!InLobby || Lobby == CSteamID.Nil)
            {
                NetLog.Warning(LogSource, "InviteToLobby called while not in a lobby.");
                return;
            }

            if (steamId64 == 0UL)
            {
                NetLog.Warning(LogSource, "InviteToLobby called with invalid target Steam64 id 0.");
                return;
            }

            try { SteamMatchmaking.InviteUserToLobby(Lobby, new CSteamID(steamId64)); }
            catch (Exception ex) { NetLog.Error(LogSource, $"InviteToLobby failed for target {steamId64}: {ex}"); }
        }

        void OnLobbyEnter(LobbyEnter_t param)
        {
            NetLog.Debug(LogSource, $"LobbyEnter {param.m_ulSteamIDLobby}");
            Lobby = new CSteamID(param.m_ulSteamIDLobby);
            InLobby = true;
            RefreshPlayerList();
            LobbyEntered?.Invoke();
        }

        void OnLobbyCreated(LobbyCreated_t param)
        {
            NetLog.Debug(LogSource, $"LobbyCreated: {param.m_eResult}");
            if (param.m_eResult == EResult.k_EResultOK)
            {
                Lobby = new CSteamID(param.m_ulSteamIDLobby);
                InLobby = true;
                RefreshPlayerList();
                LobbyCreated?.Invoke();
            }
            else
            {
                NetLog.Error(LogSource, $"Lobby creation failed: {param.m_eResult}");
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
                    PlayerEntered?.Invoke(player.m_SteamID);
                }

                var leftMask =
                    EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                    EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                    EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                    EChatMemberStateChange.k_EChatMemberStateChangeBanned;

                if ((change & leftMask) != 0)
                {
                    CleanupPeerState(player.m_SteamID);
                    PlayerLeft?.Invoke(player.m_SteamID);
                }
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"OnLobbyChatUpdate error: {ex}");
            }
        }

        internal void CleanupPeerState(ulong steamId64)
        {
            if (steamId64 == 0UL) return;

            lastPlayerData.Remove(new CSteamID(steamId64));
            lock (lastSeenSequence) lastSeenSequence.Remove(steamId64);
            lock (rateLimiters) rateLimiters.Remove(steamId64);
            lock (cryptoStateLock)
            {
                handshakeStates.Remove(steamId64);
                if (!perPeerSymmetricKey.TryGetValue(steamId64, out var sym) || sym == null) return;
                CryptographicOperations.ZeroMemory(sym);
                perPeerSymmetricKey.Remove(steamId64);
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
            lock (fragmentLock)
            {
                fragmentBuffers.Clear();
                staleFragmentKeys.Clear();
                nextFragmentCleanupAt = DateTime.MinValue;
            }
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

                if (Lobby == CSteamID.Nil)
                {
                    players = Array.Empty<CSteamID>();
                    return;
                }

                int count = getNumLobbyMembers(Lobby);
                players = new CSteamID[count];

                for (int i = 0; i < players.Length; i++)
                {
                    players[i] = getLobbyMemberByIndex(Lobby, i);
                }
                NetLog.Debug(LogSource, $"RefreshPlayerList: total members = {players.Length}");
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"RefreshPlayerList error: {ex}");
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

        public void RegisterLobbyDataKey(string key)
        {
            if (lobbyDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Lobby key {key} already registered");
            else lobbyDataKeys.Add(key);
        }

        public void SetLobbyData(string key, object value)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot set lobby data when not in lobby."); return; }
            if (!lobbyDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Accessing unregistered lobby key '{key}'.");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            try
            {
                setLobbyData(Lobby, key, serialized);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"SetLobbyData failed for key '{key}': {ex}");
            }
        }

        public T GetLobbyData<T>(string key)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot get lobby data when not in lobby."); return default(T)!; }
            if (!lobbyDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Accessing unregistered lobby key '{key}'.");
            string v;
            try
            {
                v = getLobbyData(Lobby, key);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"GetLobbyData failed for key '{key}': {ex}");
                return default(T)!;
            }
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { NetLog.Error(LogSource, $"Could not parse lobby data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        public void RegisterPlayerDataKey(string key)
        {
            if (playerDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Player key {key} already registered");
            else playerDataKeys.Add(key);
        }

        public void SetPlayerData(string key, object value)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot set player data when not in lobby."); return; }
            if (!playerDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Accessing unregistered player key '{key}'.");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            try
            {
                setLobbyMemberData(Lobby, key, serialized);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"SetPlayerData failed for key '{key}': {ex}");
            }
        }

        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot get player data when not in lobby."); return default(T)!; }
            if (!playerDataKeys.Contains(key)) NetLog.Warning(LogSource, $"Accessing unregistered player key '{key}'.");
            var player = new CSteamID(steamId64);
            string v;
            try
            {
                v = getLobbyMemberData(Lobby, player, key);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"GetPlayerData failed for key '{key}' and player '{steamId64}': {ex}");
                return default(T)!;
            }
            if (string.IsNullOrEmpty(v)) return default(T)!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { NetLog.Error(LogSource, $"Could not parse player data [{key},{v}] as {typeof(T).Name}"); return default(T)!; }
        }

        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return RegisterNetworkTypeInternal(instance.GetType(), instance, modId, mask);
        }
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
                        .FirstOrDefault(method => method.IsDefined(typeof(CustomRPCAttribute), inherit: false));
                    if (instanceRpc != null) throw new InvalidOperationException($"Cannot register instance RPC method {type.FullName}.{instanceRpc.Name} without an instance.");
                }

                var methods = type.GetMethods(registrationFlags);
                foreach (var method in methods)
                {
                    if (!method.IsDefined(typeof(CustomRPCAttribute), inherit: false)) continue;

                    if (!rpcs.ContainsKey(modId)) rpcs[modId] = new Dictionary<string, List<MessageHandler>>();
                    if (!rpcs[modId].ContainsKey(method.Name)) rpcs[modId][method.Name] = new List<MessageHandler>();
                    var handlers = rpcs[modId][method.Name];
                    var alreadyRegisteredStatic = instance == null
                        && handlers.Any(existing => existing.Mask == mask && existing.Method == method);
                    if (alreadyRegisteredStatic) continue;

                    var mh = new MessageHandler
                    {
                        Target = method.IsStatic ? null! : instance!,
                        Method = method,
                        Parameters = method.GetParameters(),
                        TakesInfo = false,
                        Mask = mask
                    };
                    mh.TakesInfo = mh.Parameters.Length > 0 && RpcInfoParameterTypeHelper.IsRpcInfoParameterType(mh.Parameters.Last().ParameterType);
                    mh.ParameterCountWithoutRpcInfo = mh.TakesInfo ? mh.Parameters.Length - 1 : mh.Parameters.Length;
                    mh.OverloadKey = BuildOverloadKey(mh);
                    handlers.Add(mh);
                    registeredHandlers.Add(new HandlerRegistration { MethodName = method.Name, Handler = mh });
                    registered++;
                }
            }

            if (instance != null)
                NetLog.Info(LogSource, $"Registered {registered} RPCs for mod {modId} on {instance} ({instance.GetType().FullName})");
            else
                NetLog.Info(LogSource, $"Registered {registered} static RPCs for mod {modId} on type {type.FullName}");

            return new RegistrationToken(this, modId, registeredHandlers);
        }

        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            DeregisterNetworkObjectInternal(instance.GetType(), instance, modId, mask);
        }
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
                    NetLog.Warning(LogSource, $"No RPCs for mod {modId}");
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

                NetLog.Info(LogSource, $"Deregistered {removed} RPCs for mod {modId} (type/instance {type.FullName})");
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

        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { NetLog.Error(LogSource, "RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;

            foreach (var p in players)
            {
                if (p == SteamUser.GetSteamID())
                {
                    continue;
                }
                EnqueueOrSend(BuildFramedBytesWithMeta(msg, modId, reliable), p, reliable, DeterminePriority(methodName));
            }

            InvokeLocalMessage(new Message(msg.ToArray(), messageSizePolicy), SteamUser.GetSteamID());
        }

        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { NetLog.Error(LogSource, "RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;

            foreach (var p in players)
            {
                if (p == SteamUser.GetSteamID())
                {
                    continue;
                }
                EnqueueOrSend(BuildFramedBytesWithMeta(msg, modId, reliable), p, reliable, DeterminePriority(methodName));
            }

            InvokeLocalMessage(new Message(msg.ToArray(), messageSizePolicy), SteamUser.GetSteamID());
        }

        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters)
        {
            RPCTarget(modId, methodName, new CSteamID(targetSteamId64), reliable, parameters);
        }

        public void RPCTarget(uint modId, string methodName, CSteamID target, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            if (target == SteamUser.GetSteamID())
            {
                InvokeLocalMessage(new Message(msg.ToArray(), messageSizePolicy), SteamUser.GetSteamID());
                return;
            }
            var framed = BuildFramedBytesWithMeta(msg, modId, reliable);
            EnqueueOrSend(framed, target, reliable, DeterminePriority(methodName));
        }

        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            RPCTarget(modId, methodName, new CSteamID(targetSteamId64), reliable, parameterTypes, parameters);
        }

        public void RPCTarget(uint modId, string methodName, CSteamID target, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            if (target == SteamUser.GetSteamID())
            {
                InvokeLocalMessage(new Message(msg.ToArray(), messageSizePolicy), SteamUser.GetSteamID());
                return;
            }
            var framed = BuildFramedBytesWithMeta(msg, modId, reliable);
            EnqueueOrSend(framed, target, reliable, DeterminePriority(methodName));
        }

        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { NetLog.Error(LogSource, "Not in lobby"); return; }
            var host = SteamMatchmaking.GetLobbyOwner(Lobby);
            if (host == CSteamID.Nil) { NetLog.Error(LogSource, "No host set"); return; }
            RPCTarget(modId, methodName, host, reliable, parameters);
        }

        Priority DeterminePriority(string methodName)
        {
            if (methodName.IndexOf("admin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                methodName.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                methodName.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0 ||
                methodName.IndexOf("sync", StringComparison.OrdinalIgnoreCase) >= 0)
                return Priority.High;
            return Priority.Normal;
        }

        void EnqueueOrSend(byte[] framed, CSteamID target, ReliableType reliable, Priority p)
        {
            var rl = GetOrCreateRateLimiter(target.m_SteamID);
            if (!rl.Allowed())
            {
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
                var maxDepth = p == Priority.Normal ? MAX_NORMAL_QUEUE_DEPTH : MAX_LOW_QUEUE_DEPTH;
                if (q.Count >= maxDepth)
                {
                    q.Dequeue();
                    var queueName = p == Priority.Normal ? nameof(normalQueue) : nameof(lowQueue);
                    var key = $"EnqueueOrSend.Overflow.{queueName}";
                    if (NetLog.TryEnterCooldown(key, QueueOverflowWarningCooldownSeconds))
                    {
                        NetLog.Warning(LogSource, $"Outbound {queueName} overflow (max={maxDepth}), dropped oldest message.");
                    }
                }
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
            if (compress) payload = msg.CompressPayload(payload);

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
            if (data.Length > messageSizePolicy.MaxSize)
            {
                NetLog.Error(LogSource, $"Send length {data.Length} exceeds configured max {messageSizePolicy.MaxSize}");
                return;
            }

            if (target == SteamUser.GetSteamID())
            {
                ProcessIncomingFrame(data, SteamUser.GetSteamID());
                return;
            }

            var id = new SteamNetworkingIdentity();
            id.SetSteamID(target);
            GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr p = pinned.AddrOfPinnedObject();

                int flags = Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession | ResolveSendModeFlag(reliable);

                var res = SteamNetworkingMessages.SendMessageToUser(ref id, p, (uint)data.Length, flags, CHANNEL);
                if (res != EResult.k_EResultOK)
                {
                    NetLog.Error(LogSource, $"SendMessageToUser failed: {res} to {target}");
                }
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"SendBytes exception: {ex}");
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }
        }

        static int ResolveSendModeFlag(ReliableType reliable)
        {
            switch (reliable)
            {
                case ReliableType.Unreliable: return Constants.k_nSteamNetworkingSend_Unreliable;
                case ReliableType.Reliable: return Constants.k_nSteamNetworkingSend_Reliable;
                case ReliableType.UnreliableNoDelay: return Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
                default:
                    NetLog.Warning(LogSource, $"Unknown {nameof(ReliableType)} value '{reliable}' ({(int)reliable}).");
#if DEBUG
                    throw new InvalidEnumArgumentException(nameof(reliable), (int)reliable, typeof(ReliableType));
#else
                    NetLog.Warning(LogSource, $"Falling back to {ReliableType.Reliable} send mode.");
                    return Constants.k_nSteamNetworkingSend_Reliable;
#endif
            }
        }

        public void PollReceive()
        {
            FlushQueues();
            RetransmitUnacked();
            ReceiveMessages();
        }

        void RetransmitUnacked()
        {
            (ulong target, ulong msgId)[]? keysBuffer = null;
            UnackedMessage[]? retransmitBuffer = null;
            int retransmitCount = 0;
            lock (unackedLock)
            {
                var now = DateTime.UtcNow;
                if (unacked.Count == 0)
                {
                    return;
                }

                keysBuffer = ArrayPool<(ulong target, ulong msgId)>.Shared.Rent(unacked.Count);
                int keyCount = 0;
                foreach (var key in unacked.Keys)
                {
                    keysBuffer[keyCount++] = key;
                }

                retransmitBuffer = ArrayPool<UnackedMessage>.Shared.Rent(keyCount);
                for (int i = 0; i < keyCount; i++)
                {
                    var key = keysBuffer[i];
                    if (!unacked.TryGetValue(key, out var info)) continue;
                    if (now - info.LastSent > ackTimeout)
                    {
                        if (info.Attempts >= maxRetransmitAttempts)
                        {
                            unacked.Remove(key);
                        }
                        else
                        {
                            info.Attempts++;
                            info.LastSent = now;
                            unacked[key] = info;
                            retransmitBuffer[retransmitCount++] = info;
                        }
                    }
                }
            }

            try
            {
                for (int i = 0; i < retransmitCount; i++)
                {
                    var item = retransmitBuffer![i];
                    try
                    {
                        SendBytes(item.Framed, item.Target, item.Reliable);
                    }
                    catch (Exception ex)
                    {
                        NetLog.Error(LogSource, $"RetransmitUnacked: retransmit SendBytes exception: {ex}");
                    }
                }
            }
            finally
            {
                if (keysBuffer != null) ArrayPool<(ulong target, ulong msgId)>.Shared.Return(keysBuffer);
                if (retransmitBuffer != null)
                {
                    Array.Clear(retransmitBuffer, 0, retransmitCount);
                    ArrayPool<UnackedMessage>.Shared.Return(retransmitBuffer);
                }
            }
        }

        void ReceiveMessages()
        {
            try
            {
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(CHANNEL, this.inMessages, MAX_IN_MESSAGES);
                if (count <= 0) return;

                for (int i = 0; i < count; i++)
                {
                    IntPtr outPtr = this.inMessages[i];
                    if (outPtr == IntPtr.Zero) continue;
                    try
                    {
                        SteamNetworkingMessage_t steamMsg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(outPtr);
                        int size = (int)steamMsg.m_cbSize;
                        if (size <= 0) continue;
                        if (size > messageSizePolicy.MaxSize) continue;

                        CSteamID sender = steamMsg.m_identityPeer.GetSteamID();
                        if (sender == CSteamID.Nil) continue;

                        byte[] bytes = new byte[size];
                        Marshal.Copy(steamMsg.m_pData, bytes, 0, size);

                        try
                        {
                            ProcessIncomingFrame(bytes, sender);
                        }
                        catch (Exception ex)
                        {
                            NetLog.Error(LogSource, $"ProcessIncomingFrame exception: {ex}");
                        }
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(outPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"ReceiveMessages outer exception: {ex}");
            }
        }

        void ProcessIncomingFrame(byte[] frame, CSteamID sender)
        {
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
                    NetLog.Warning(LogSource, $"ProcessIncomingFrame: malformed fragment header total={total} index={index} from {sender}");
                    return;
                }


                int remainingHeader = (int)(msHeader.Length - msHeader.Position);
                if (remainingHeader <= 0)
                {
                    return;
                }

                var payloadFragment = new byte[remainingHeader];
                msHeader.Read(payloadFragment, 0, remainingHeader);

                byte[] assembledPayload;
                if (total > 1)
                {
                    var key = (sender.m_SteamID, msgId);
                    FragmentBuffer completedBuffer;
                    var nowUtc = DateTime.UtcNow;

                    lock (fragmentLock)
                    {
                        if (!fragmentBuffers.TryGetValue(key, out var fb))
                        {
                            fb = new FragmentBuffer { Total = total, FirstSeen = nowUtc };
                            fragmentBuffers[key] = fb;
                        }

                        fb.Fragments[index] = payloadFragment;

                        if (nowUtc >= nextFragmentCleanupAt)
                        {
                            nextFragmentCleanupAt = nowUtc + FragmentCleanupInterval;
                            staleFragmentKeys.Clear();
                            foreach (var entry in fragmentBuffers)
                            {
                                if (nowUtc - entry.Value.FirstSeen <= FragmentTimeout) continue;
                                staleFragmentKeys.Add(entry.Key);
                            }

                            for (int i = 0; i < staleFragmentKeys.Count; i++)
                            {
                                fragmentBuffers.Remove(staleFragmentKeys[i]);
                            }
                            staleFragmentKeys.Clear();
                        }

                        if (fb.Fragments.Count != fb.Total)
                        {
                            return;
                        }

                        completedBuffer = fb;
                        fragmentBuffers.Remove(key);
                    }

                    using var outMs = new MemoryStream();
                    for (int i = 0; i < completedBuffer.Total; i++)
                    {
                        if (!completedBuffer.Fragments.TryGetValue(i, out var part))
                        {
                            return;
                        }

                        outMs.Write(part, 0, part.Length);
                    }

                    assembledPayload = outMs.ToArray();
                }
                else
                {
                    assembledPayload = payloadFragment;
                }

                byte[] payloadWithOptionalMacAndSig = assembledPayload;
                byte[] payloadToProcess = payloadWithOptionalMacAndSig;

                if (hasSign)
                {
                    if (payloadToProcess.Length < 3)
                    {
                        return;
                    }

                    RSAParameters[] publicKeys;
                    lock (cryptoStateLock)
                    {
                        publicKeys = modPublicKeysSnapshot;
                        if (publicKeys.Length == 0)
                        {
                            return;
                        }
                    }

                    bool verified = false;
                    for (int i = 0; i < publicKeys.Length; i++)
                    {
                        var rsaParams = publicKeys[i];
                        int expectedSigLen = rsaParams.Modulus?.Length ?? 0;
                        if (expectedSigLen <= 0 || payloadToProcess.Length < expectedSigLen + 2)
                        {
                            continue;
                        }

                        int sigSectionStart = payloadToProcess.Length - expectedSigLen - 2;
                        if (sigSectionStart < 0)
                        {
                            continue;
                        }

                        ushort declaredLen = BinaryPrimitives.ReadUInt16LittleEndian(payloadToProcess.AsSpan(sigSectionStart, 2));
                        if (declaredLen != expectedSigLen)
                        {
                            continue;
                        }

                        var signature = new byte[expectedSigLen];
                        Array.Copy(payloadToProcess, sigSectionStart + 2, signature, 0, expectedSigLen);
                        var dataOnly = new byte[sigSectionStart];
                        Array.Copy(payloadToProcess, 0, dataOnly, 0, sigSectionStart);

                        try
                        {
                            using var rsa = RSA.Create();
                            rsa.ImportParameters(rsaParams);
                            if (!rsa.VerifyData(dataOnly, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                            {
                                continue;
                            }

                            payloadToProcess = dataOnly;
                            verified = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            NetLog.Error(LogSource, $"ProcessIncomingFrame: Signature verification error: {ex}");
                        }
                    }

                    if (!verified)
                    {
                        return;
                    }
                }

                if (compressed)
                {
                    try
                    {
                        payloadToProcess = Message.DecompressPayload(payloadToProcess, messageSizePolicy.MaxLogicalSize);
                    }
                    catch (Exception ex)
                    {
                        NetLog.Error(LogSource, $"ProcessIncomingFrame: Decompression failed: {ex}");
                        return;
                    }
                }

                var message = new Message(payloadToProcess, messageSizePolicy);

                if (message.ModID == 0)
                {
                    HandleInternalMessage(message, sender, msgId, seq, requiresAck);
                    if (requiresAck && message.MethodName != "NETWORK_INTERNAL_ACK")
                    {
                        SendAckToSender(sender, msgId);
                    }
                    return;
                }

                var sender64 = sender.m_SteamID;
                var rl = GetOrCreateRateLimiter(sender64);
                if (!rl.IncomingAllowed())
                {
                    return;
                }

                if (!CheckAndUpdateSequence(sender64, message.ModID, seq))
                {
                    return;
                }

                if (requiresAck)
                {
                    SendAckToSender(sender, msgId);
                }

                if (IncomingValidator != null && !IncomingValidator(message, sender64))
                {
                    return;
                }

                DispatchIncoming(message, sender);
            }
            catch (EndOfStreamException ex)
            {
                NetLog.Warning(LogSource, $"ProcessIncomingFrame: malformed frame from {sender}, dropping. {ex.Message}");
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"ProcessIncomingFrame top-level exception: {ex}");
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
            var ackMsg = new Message(0u, "NETWORK_INTERNAL_ACK", 0, messageSizePolicy);
            ackMsg.WriteULong(msgId);
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
                        NetLog.Warning(LogSource, $"Unknown internal method {message.MethodName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"HandleInternalMessage error: {ex}");
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
                    NetLog.Warning(LogSource, $"Dropping message for unknown mod {message.ModID}");
                    return;
                }
                if (!methods.TryGetValue(message.MethodName, out var handlers))
                {
                    NetLog.Warning(LogSource, $"Dropping message for method {message.MethodName} not registered for {message.ModID}");
                    return;
                }
                handlersSnapshot = handlers.ToArray();
            }

            MessageHandler? chosenHandler = null;
            object[]? chosenParams = null;
            MessageHandler? fallbackHandler = null;
            object[]? fallbackParams = null;
            bool hasOverloadKey = !string.IsNullOrEmpty(message.OverloadKey);
            bool hasPreferredOverload = false;
            if (hasOverloadKey)
            {
                for (int i = 0; i < handlersSnapshot.Length; i++)
                {
                    var handler = handlersSnapshot[i];
                    if (handler.Mask != message.Mask) continue;
                    if (handler.OverloadKey != message.OverloadKey) continue;
                    hasPreferredOverload = true;
                    break;
                }
            }

            void TryDispatch(bool? preferOverloadKeyMatch)
            {
                for (int i = 0; i < handlersSnapshot.Length; i++)
                {
                    var handler = handlersSnapshot[i];
                    if (handler.Mask != message.Mask) continue;
                    if (preferOverloadKeyMatch.HasValue)
                    {
                        bool isPreferred = handler.OverloadKey == message.OverloadKey;
                        if (preferOverloadKeyMatch.Value != isPreferred) continue;
                    }

                    if (!TryDeserializeForHandler(message, handler, sender, isLocalLoopback: false, out var callParams, out int unread))
                        continue;

                    if (unread == 0)
                    {
                        chosenHandler = handler;
                        chosenParams = callParams;
                        return;
                    }

                    fallbackHandler ??= handler;
                    fallbackParams ??= callParams;
                }
            }

            if (hasPreferredOverload)
            {
                TryDispatch(preferOverloadKeyMatch: true);
                if (chosenHandler == null) TryDispatch(preferOverloadKeyMatch: false);
            }
            else
            {
                TryDispatch(preferOverloadKeyMatch: null);
            }

            if (chosenHandler == null)
            {
                chosenHandler = fallbackHandler;
                chosenParams = fallbackParams;
            }

            if (chosenHandler == null || chosenParams == null)
            {
                NetLog.Warning(LogSource, $"No handler matched for {message.ModID}:{message.MethodName} mask={message.Mask}");
                return;
            }

            try
            {
                chosenHandler.Method.Invoke(chosenHandler.Target, chosenParams);
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"Invoke RPC error: {ex}");
            }
        }

        object CreateRpcInfoInstance(Type infoType, CSteamID sender, bool isLocalLoopback)
        {
            try
            {
                var ctorFull = infoType.GetConstructor(new Type[] { typeof(ulong), typeof(string), typeof(bool) });
                if (ctorFull != null) return ctorFull.Invoke(new object[] { sender.m_SteamID, sender.ToString(), isLocalLoopback });

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
            catch (Exception ex)
            {
                NetLog.DebugThrottled(LogSource, "CreateRpcInfoInstance", DebugLogCooldownSeconds, $"CreateRpcInfoInstance failed for {infoType?.FullName ?? "<unknown type>"}: {ex.GetType().Name}: {ex.Message}");
            }
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

            var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0, messageSizePolicy);
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
                handshakeStates.TryGetValue(sender.m_SteamID, out var state);
                state ??= new HandshakeState();
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
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_PUBKEY", 0, messageSizePolicy);
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
                using var rsaPeer = RSA.Create();
                var rsaParams = DeserializeRsaPublicKey(peerPubForSecret);
                rsaPeer.ImportParameters(rsaParams);
                enc = rsaPeer.Encrypt(sym, RSAEncryptionPadding.Pkcs1);
            }
            catch (Exception ex)
            {
                NetLog.Warning(LogSource, $"Handshake reply rejected invalid peer RSA key from {sender}: {ex.Message}");
                CryptographicOperations.ZeroMemory(sym);
                return;
            }

            var m2 = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_SECRET", 0, messageSizePolicy);
            m2.WriteBytes(enc);
            m2.WriteString(localNonceForSecret);
            var framed2 = BuildFramedBytesWithMeta(m2, 0, ReliableType.Reliable);
            SendBytes(framed2, sender, ReliableType.Reliable);

            lock (cryptoStateLock)
            {
                handshakeStates.TryGetValue(sender.m_SteamID, out var state);
                state ??= new HandshakeState();
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
                var sym = LocalRsa.Decrypt(encSecret, RSAEncryptionPadding.Pkcs1);
                lock (cryptoStateLock)
                {
                    handshakeStates.TryGetValue(sender.m_SteamID, out var state);
                    state ??= new HandshakeState();
                    state.Sym = sym;
                    state.Completed = true;
                    handshakeStates[sender.m_SteamID] = state;
                    perPeerSymmetricKey[sender.m_SteamID] = sym;
                }

                var confirm = HmacSha256Raw(sym, Encoding.UTF8.GetBytes(initiatorNonce));
                var m = new Message(0u, "NETWORK_INTERNAL_HANDSHAKE_CONFIRM", 0, messageSizePolicy);
                m.WriteString(initiatorNonce);
                m.WriteBytes(confirm);
                var framed = BuildFramedBytesWithMeta(m, 0, ReliableType.Reliable);
                SendBytes(framed, sender, ReliableType.Reliable);
            }
            catch (Exception ex) { NetLog.Error(LogSource, $"Handshake decryption error: {ex}"); }
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
                NetLog.Warning(LogSource, "Handshake confirm received but no state");
                return;
            }

            var expected = HmacSha256Raw(sym, Encoding.UTF8.GetBytes(initiatorNonce));
            if (!expected.SequenceEqual(confirmHmac))
            {
                NetLog.Warning(LogSource, "Handshake confirm HMAC mismatch");
                return;
            }

            lock (cryptoStateLock)
            {
                if (!handshakeStates.TryGetValue(sender.m_SteamID, out var current) || current.Sym == null) return;
                if (!current.Sym.SequenceEqual(sym))
                {
                    NetLog.Warning(LogSource, "Handshake state changed before confirm commit; discarding stale confirmation");
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

        static string SerializeRsaPublicKey(RSA rsa)
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

        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate)
        {
            if (signerDelegate == null) throw new ArgumentNullException(nameof(signerDelegate));
            lock (cryptoStateLock)
            {
                modSigners[modId] = signerDelegate;
            }
        }
        public void RegisterModPublicKey(uint modId, RSAParameters pub)
        {
            if (pub.Modulus == null || pub.Modulus.Length == 0)
                throw new ArgumentException("RSA public key modulus must not be empty.", nameof(pub));
            if (pub.Exponent == null || pub.Exponent.Length == 0)
                throw new ArgumentException("RSA public key exponent must not be empty.", nameof(pub));
            lock (cryptoStateLock)
            {
                modPublicKeys[modId] = CloneRsaParameters(pub);
                modPublicKeysSnapshot = BuildModPublicKeySnapshotUnderLock();
            }
        }

        static RSAParameters CloneRsaParameters(RSAParameters source)
        {
            return new RSAParameters
            {
                Modulus = source.Modulus == null ? null : (byte[])source.Modulus.Clone(),
                Exponent = source.Exponent == null ? null : (byte[])source.Exponent.Clone()
            };
        }

        RSAParameters[] BuildModPublicKeySnapshotUnderLock()
        {
            var snapshot = new RSAParameters[modPublicKeys.Count];
            int index = 0;
            foreach (var key in modPublicKeys.Values)
            {
                snapshot[index++] = CloneRsaParameters(key);
            }

            return snapshot;
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

            var deserialized = new List<(MessageHandler Handler, object[] CallParams, int Unread, string OverloadKey)>();
            bool hasOverloadKey = !string.IsNullOrEmpty(message.OverloadKey);
            bool hasPreferredOverload = false;
            if (hasOverloadKey)
            {
                for (int i = 0; i < handlersSnapshot.Length; i++)
                {
                    var handler = handlersSnapshot[i];
                    if (handler.Mask != message.Mask) continue;
                    if (handler.OverloadKey != message.OverloadKey) continue;
                    hasPreferredOverload = true;
                    break;
                }
            }

            void CollectCandidates(bool? preferOverloadKeyMatch)
            {
                for (int i = 0; i < handlersSnapshot.Length; i++)
                {
                    var handler = handlersSnapshot[i];
                    if (handler.Mask != message.Mask) continue;
                    if (preferOverloadKeyMatch.HasValue)
                    {
                        bool isPreferred = handler.OverloadKey == message.OverloadKey;
                        if (preferOverloadKeyMatch.Value != isPreferred) continue;
                    }

                    if (!TryDeserializeForHandler(message, handler, localSender, isLocalLoopback: true, out var callParams, out int unread))
                        continue;

                    deserialized.Add((handler, callParams, unread, handler.OverloadKey));
                }
            }

            if (hasPreferredOverload)
            {
                CollectCandidates(preferOverloadKeyMatch: true);
                CollectCandidates(preferOverloadKeyMatch: false);
            }
            else
            {
                CollectCandidates(preferOverloadKeyMatch: null);
            }

            if (deserialized.Count == 0)
            {
                NetLog.Warning(LogSource, $"No handler matched for local {message.ModID}:{message.MethodName} mask={message.Mask}");
                return;
            }

            int chosenIndex = deserialized.FindIndex(c => c.Unread == 0);
            if (chosenIndex < 0) chosenIndex = 0;
            string chosenOverloadKey = deserialized[chosenIndex].OverloadKey;

            bool invokedAny = false;
            for (int i = 0; i < deserialized.Count; i++)
            {
                var candidate = deserialized[i];
                if (candidate.OverloadKey != chosenOverloadKey) continue;
                try
                {
                    candidate.Handler.Method.Invoke(candidate.Handler.Target, candidate.CallParams);
                    invokedAny = true;
                }
                catch (Exception ex)
                {
                    NetLog.Error(LogSource, $"Local invoke error: {ex}");
                }
            }

            if (!invokedAny)
                NetLog.Warning(LogSource, $"No handler matched for local {message.ModID}:{message.MethodName} mask={message.Mask}");
        }

        bool TryDeserializeForHandler(Message source, MessageHandler handler, CSteamID sender, bool isLocalLoopback, out object[] callParams, out int unread)
        {
            callParams = null!;
            unread = int.MaxValue;
            var cursor = source.SaveReadCursor();
            try
            {
                var paramInfos = handler.Parameters;
                int paramCount = handler.ParameterCountWithoutRpcInfo;
                callParams = new object[paramInfos.Length];

                for (int i = 0; i < paramCount; i++) callParams[i] = source.ReadObject(paramInfos[i].ParameterType);
                if (handler.TakesInfo)
                {
                    var infoType = paramInfos[paramInfos.Length - 1].ParameterType;
                    callParams[paramInfos.Length - 1] = CreateRpcInfoInstance(infoType, sender, isLocalLoopback);
                }

                unread = source.UnreadLength();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                source.RestoreReadCursor(cursor);
            }
        }

        Message? BuildMessage(uint modId, string methodName, int mask, object?[] parameters, Type[]? parameterTypes)
        {
            try
            {
                var msg = new Message(modId, methodName, mask, messageSizePolicy);
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
                        int expectedCount = h.ParameterCountWithoutRpcInfo;
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
                            return h.ParameterCountWithoutRpcInfo == parameters.Length && h.Mask == mask;
                        });
                    }

                    if (chosen == null)
                    {
                        NetLog.Error(LogSource, $"No RPC overload matched method '{methodName}' for mask {mask} and parameter list.");
                        return null;
                    }

                    msg = new Message(modId, methodName, mask, chosen.OverloadKey, messageSizePolicy);
                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.ParameterCountWithoutRpcInfo;
                    for (int i = 0; i < expectedCountFinal; i++)
                    {
                        var t = expectedParams[i].ParameterType;
                        var p = parameters[i];
                        if (p == null)
                        {
                            if (t.IsValueType && Nullable.GetUnderlyingType(t) == null)
                                throw new ArgumentNullException(nameof(parameters), $"Parameter {i} for {methodName} cannot be null; expected non-nullable {t}.");
                            msg.WriteObject(t, null!);
                            continue;
                        }
                        if (!t.IsAssignableFrom(p.GetType()))
                            throw new ArgumentException($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}", nameof(parameters));
                        msg.WriteObject(t, p);
                    }
                }
                else
                {
                    if (parameterTypes != null)
                    {
                        if (parameterTypes.Length != parameters.Length)
                            throw new ArgumentException($"Parameter type count mismatch: expected {parameterTypes.Length}, got {parameters.Length}", nameof(parameters));
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            var t = parameterTypes[i];
                            var p = parameters[i];
                            if (p == null)
                            {
                                if (t.IsValueType && Nullable.GetUnderlyingType(t) == null)
                                    throw new ArgumentNullException(nameof(parameters), $"Parameter {i} for {methodName} cannot be null; expected non-nullable {t}.");
                                msg.WriteObject(t, null!);
                                continue;
                            }
                            if (!t.IsAssignableFrom(p.GetType()))
                                throw new ArgumentException($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}", nameof(parameters));
                            msg.WriteObject(t, p);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            var p = parameters[i] ?? throw new ArgumentNullException(nameof(parameters), $"Parameter {i} is null for unregistered RPC {methodName}; use typed RPC overload.");
                            msg.WriteObject(p.GetType(), p);
                        }
                    }
                }

                if (msg.Length() > messageSizePolicy.MaxLogicalSize)
                {
                    NetLog.Error(LogSource, "Message exceeds maximum allowed overall size.");
                    return null;
                }

                return msg;
            }
            catch (Exception ex)
            {
                NetLog.Error(LogSource, $"BuildMessage failed: {ex}");
                return null;
            }
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
            readonly object qLock = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool Allowed()
            {
                lock (qLock)
                {
                    var now = DateTime.UtcNow;
                    while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
                    if (q.Count >= limit) return false;
                    q.Enqueue(now);
                    return true;
                }
            }
            public bool IncomingAllowed() => Allowed();
        }

        enum Priority { High = 0, Normal = 1, Low = 2 }
        class QueuedSend { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime Enqueued; }
        class UnackedMessage { public byte[] Framed = null!; public CSteamID Target; public ReliableType Reliable; public DateTime LastSent; public int Attempts; public ulong msgId => BinaryPrimitives.ReadUInt64LittleEndian(Framed.AsSpan(1, 8)); }
        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; public int ParameterCountWithoutRpcInfo; public string OverloadKey = string.Empty; }

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
        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) => offline.RPC(modId, methodName, reliable, parameterTypes, parameters);
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters) => offline.RPCTarget(modId, methodName, targetSteamId64, reliable, parameters);
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters) => offline.RPCTarget(modId, methodName, targetSteamId64, reliable, parameterTypes, parameters);
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
