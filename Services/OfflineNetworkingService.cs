using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetworkingLibrary.Modules;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    public class OfflineNetworkingService : INetworkingService, INetworkingServiceStateTransfer
    {
        public bool IsInitialized { get; private set; }
        public bool InLobby { get; private set; }
        public ulong HostSteamId64 { get; private set; } = 1000UL;
        public string HostIdString => HostSteamId64.ToString();
        public event Action? LobbyCreated;
        public event Action? LobbyEntered;
        public event Action? LobbyLeft;
        public event Action<ulong>? PlayerEntered;
        public event Action<ulong>? PlayerLeft;
        public event Action<string[]>? LobbyDataChanged;
        public event Action<ulong, string[]>? PlayerDataChanged;

        public Func<Message, ulong, bool>? IncomingValidator { get; set; }

        private readonly object rpcLock = new object();
        private readonly object cryptoStateLock = new();
        readonly Dictionary<uint, Dictionary<string, List<MessageHandler>>> rpcs = new();
        readonly Dictionary<string, string> lobbyData = new();
        readonly Dictionary<ulong, Dictionary<string, string>> perPlayerData = new();
        readonly HashSet<string> lobbyKeys = new();
        readonly HashSet<string> playerKeys = new();

        readonly SlidingWindowRateLimiter rateLimiter = new(100, TimeSpan.FromSeconds(1));
        private readonly MessageSizePolicy messageSizePolicy;

        readonly Dictionary<ulong, byte[]> perPeerSymmetricKey = new();
        byte[]? globalSharedSecret;
        HMACSHA256? globalHmac;
        readonly Dictionary<uint, Func<byte[], byte[]>> modSigners = new();
        readonly Dictionary<uint, RSAParameters> modPublicKeys = new();
        static readonly TimeSpan LogFallbackCooldown = TimeSpan.FromSeconds(5);
        static readonly object logFallbackLock = new();
        static DateTime lastLogFallbackUtc = DateTime.MinValue;
        static int suppressedLogFallbackCount;
        static readonly TimeSpan DeserializeFailureCooldown = TimeSpan.FromSeconds(2);
        static readonly object deserializeFailureLock = new();
        static DateTime lastDeserializeFailureUtc = DateTime.MinValue;
        static int suppressedDeserializeFailureCount;
        static readonly TimeSpan ExceptionLogCooldown = TimeSpan.FromSeconds(3);
        static readonly object exceptionLogThrottleLock = new();
        static readonly Dictionary<string, DateTime> lastExceptionLogByKey = new();
        static readonly Dictionary<string, int> suppressedExceptionLogByKey = new();
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        static void LogError(string message)
        {
            try
            {
                var logger = Net.Logger;
                if (logger == null) throw new InvalidOperationException("Net logger is unavailable.");
                logger.LogError(message);
            }
            catch (Exception ex)
            {
                LogFallbackWarningThrottled("error", message, ex);
            }
        }

        static void LogWarning(string message)
        {
            try
            {
                var logger = Net.Logger;
                if (logger == null) throw new InvalidOperationException("Net logger is unavailable.");
                logger.LogWarning(message);
            }
            catch (Exception ex)
            {
                LogFallbackWarningThrottled("warning", message, ex);
            }
        }

        static void LogFallbackWarningThrottled(string level, string originalMessage, Exception ex)
        {
            var now = UtcNow();
            lock (logFallbackLock)
            {
                if (IsInCooldown(now, lastLogFallbackUtc, LogFallbackCooldown))
                {
                    suppressedLogFallbackCount++;
                    return;
                }

                var suppressed = suppressedLogFallbackCount;
                suppressedLogFallbackCount = 0;
                lastLogFallbackUtc = now;
                var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar logger failures." : string.Empty;
                var fallback = $"[OfflineNetworkingService] Failed to write {level} log. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {originalMessage}.{suffix}";
                try { Debug.LogWarning(fallback); }
                catch { System.Diagnostics.Trace.TraceWarning(fallback); }
            }
        }

        static string FormatException(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

        static void LogDeserializeFailureThrottled(Exception ex, string context)
        {
            var now = UtcNow();
            lock (deserializeFailureLock)
            {
                if (IsInCooldown(now, lastDeserializeFailureUtc, DeserializeFailureCooldown))
                {
                    suppressedDeserializeFailureCount++;
                    return;
                }

                var suppressed = suppressedDeserializeFailureCount;
                suppressedDeserializeFailureCount = 0;
                lastDeserializeFailureUtc = now;
                var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar deserialization failures." : string.Empty;
                LogWarning($"Offline RPC deserialization skipped handler ({context}). Exception: {FormatException(ex)}.{suffix}");
            }
        }

        static void LogExceptionThrottled(string key, bool error, string messagePrefix, Exception ex)
        {
            var now = UtcNow();
            lock (exceptionLogThrottleLock)
            {
                if (lastExceptionLogByKey.TryGetValue(key, out var previous) && IsInCooldown(now, previous, ExceptionLogCooldown))
                {
                    suppressedExceptionLogByKey[key] = suppressedExceptionLogByKey.TryGetValue(key, out var currentSuppressed) ? currentSuppressed + 1 : 1;
                    return;
                }

                lastExceptionLogByKey[key] = now;
                var suppressed = suppressedExceptionLogByKey.TryGetValue(key, out var count) ? count : 0;
                suppressedExceptionLogByKey.Remove(key);
                var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar exceptions." : string.Empty;
                var message = $"{messagePrefix} Exception: {FormatException(ex)}.{suffix}";
                if (error) LogError(message);
                else LogWarning(message);
            }
        }

        static bool IsInCooldown(DateTime now, DateTime previous, TimeSpan cooldown)
        {
            return previous != DateTime.MinValue && now >= previous && now - previous < cooldown;
        }

        public ulong GetLocalSteam64()
        {
            return LocalSteamId;
        }

        public ulong[] GetLobbyMemberSteamIds()
        {
            lock (rpcLock)
            {
                if (!InLobby || perPlayerData.Count == 0) return Array.Empty<ulong>();

                var memberIds = new ulong[perPlayerData.Count];
                var index = 0;
                foreach (var steamId in perPlayerData.Keys) memberIds[index++] = steamId;
                Array.Sort(memberIds);
                return memberIds;
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
            }
        }

        public void CopyRuntimeStateTo(INetworkingService target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            (object? instance, Type? type, uint modId, int mask, RegistrationTransferToken token)[] rpcRegistrations;
            string[] lobbyKeysSnapshot;
            string[] playerKeysSnapshot;
            KeyValuePair<uint, Func<byte[], byte[]>>[] signersSnapshot;
            KeyValuePair<uint, RSAParameters>[] publicKeysSnapshot;

            lock (rpcLock)
            {
                rpcRegistrations = runtimeRegistrations
                    .Select(registration => (registration.Instance, registration.Type, registration.ModId, registration.Mask, registration.Token))
                    .ToArray();
                lobbyKeysSnapshot = lobbyKeys.ToArray();
                playerKeysSnapshot = playerKeys.ToArray();
            }

            lock (cryptoStateLock)
            {
                signersSnapshot = modSigners.ToArray();
                publicKeysSnapshot = modPublicKeys
                    .Select(entry => new KeyValuePair<uint, RSAParameters>(entry.Key, CloneRsaParameters(entry.Value)))
                    .ToArray();
            }

            var migratedHandles = new List<IDisposable>();
            var originalIncomingValidator = target.IncomingValidator;
            try
            {
                target.IncomingValidator = IncomingValidator;
                foreach (var registration in rpcRegistrations)
                {
                    migratedHandles.Add(registration.type != null
                        ? target.RegisterNetworkType(registration.type, registration.modId, registration.mask)
                        : target.RegisterNetworkObject(registration.instance!, registration.modId, registration.mask));
                }
                foreach (var key in lobbyKeysSnapshot) target.RegisterLobbyDataKey(key);
                foreach (var key in playerKeysSnapshot) target.RegisterPlayerDataKey(key);
                foreach (var signer in signersSnapshot) target.RegisterModSigner(signer.Key, signer.Value);
                foreach (var publicKey in publicKeysSnapshot) target.RegisterModPublicKey(publicKey.Key, publicKey.Value);
            }
            catch
            {
                try { target.IncomingValidator = originalIncomingValidator; }
                catch { }
                for (var i = migratedHandles.Count - 1; i >= 0; i--) migratedHandles[i].Dispose();
                throw;
            }

            for (var i = 0; i < rpcRegistrations.Length; i++) rpcRegistrations[i].token.SetDisposeAction(migratedHandles[i].Dispose);
        }

        static RSAParameters CloneRsaParameters(RSAParameters source)
        {
            return new RSAParameters
            {
                Modulus = source.Modulus == null ? null : (byte[])source.Modulus.Clone(),
                Exponent = source.Exponent == null ? null : (byte[])source.Exponent.Clone()
            };
        }

        long _nextMessageId = 0;
        private ulong NextMessageId() => (ulong)System.Threading.Interlocked.Increment(ref _nextMessageId);

        public ulong LocalSteamId { get; private set; } = 1000;

        sealed class RegistrationTransferToken : IDisposable
        {
            readonly object sync = new();
            Action? onDispose;
            bool isDisposed;

            public void SetDisposeAction(Action? action)
            {
                Action? previousAction = null;
                var disposeIncomingNow = false;
                lock (sync)
                {
                    if (isDisposed) disposeIncomingNow = true;
                    else
                    {
                        previousAction = onDispose;
                        onDispose = action;
                    }
                }

                if (disposeIncomingNow)
                {
                    action?.Invoke();
                    return;
                }

                previousAction?.Invoke();
            }

            public void Dispose()
            {
                Action? action;
                lock (sync)
                {
                    if (isDisposed) return;
                    isDisposed = true;
                    action = onDispose;
                    onDispose = null;
                }

                action?.Invoke();
            }
        }

        sealed class HandlerRegistration
        {
            public string MethodName = string.Empty;
            public MessageHandler Handler = null!;
        }

        bool offlineIsHost = false;
        public bool IsHost => offlineIsHost;
        readonly List<RuntimeRegistration> runtimeRegistrations = new();

        sealed class RuntimeRegistration
        {
            public object? Instance;
            public Type? Type;
            public uint ModId;
            public int Mask;
            public RegistrationTransferToken Token = null!;
        }

        public OfflineNetworkingService(MessageSizePolicy? messageSizePolicy = null)
        {
            this.messageSizePolicy = messageSizePolicy ?? Message.DefaultSizePolicy;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            EnsureLocalPeerKey();
            IsInitialized = true;
        }

        public void Shutdown()
        {
            if (InLobby)
                LeaveLobby();

            IncomingValidator = null;
            IsInitialized = false;
            offlineIsHost = false;
            HostSteamId64 = LocalSteamId;
            lock (rpcLock)
            {
                rpcs.Clear();
                runtimeRegistrations.Clear();
                lobbyKeys.Clear();
                playerKeys.Clear();
                lobbyData.Clear();
                perPlayerData.Clear();
            }
            lock (cryptoStateLock)
            {
                modSigners.Clear();
                modPublicKeys.Clear();
                ClearPerPeerSymmetricKeysUnderLock();
                ClearGlobalSharedSecretUnderLock();
                globalHmac?.Dispose();
                globalHmac = null;
            }

            lock (exceptionLogThrottleLock)
            {
                lastExceptionLogByKey.Clear();
                suppressedExceptionLogByKey.Clear();
            }
        }

        public void CreateLobby(int maxPlayers = 8)
        {
            if (!IsInitialized)
            {
                LogError("CreateLobby called before OfflineNetworkingService.Initialize.");
                return;
            }
            if (InLobby)
            {
                LogWarning("CreateLobby called while already in a lobby. Leaving current lobby before creating a new one.");
                LeaveLobby();
            }
            if (maxPlayers != 8)
                LogWarning($"Offline mode is single-peer only; requested lobby capacity {maxPlayers} is ignored.");

            EnsureLocalPeerKey();
            lock (rpcLock)
            {
                InLobby = true;
                HostSteamId64 = LocalSteamId;
                lobbyData.Clear();
                perPlayerData.Clear();
                perPlayerData[LocalSteamId] = new Dictionary<string, string>();
                offlineIsHost = true;
            }
            LobbyCreated?.Invoke();
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
        }

        public void JoinLobby(ulong lobbySteamId64)
        {
            if (!IsInitialized)
            {
                LogError("JoinLobby called before OfflineNetworkingService.Initialize.");
                return;
            }
            if (lobbySteamId64 == 0UL)
            {
                LogWarning("JoinLobby called with invalid lobby id 0.");
                return;
            }
            if (InLobby)
            {
                LogWarning("JoinLobby called while already in a lobby. Leaving current lobby before joining a new lobby.");
                LeaveLobby();
            }

            EnsureLocalPeerKey();
            if (lobbySteamId64 != LocalSteamId)
                LogWarning($"Offline mode is single-peer only; treating JoinLobby argument {lobbySteamId64} as lobby id and preserving local host identity {LocalSteamId}.");
            lock (rpcLock)
            {
                InLobby = true;
                HostSteamId64 = LocalSteamId;
                lobbyData.Clear();
                perPlayerData.Clear();
                perPlayerData[LocalSteamId] = new Dictionary<string, string>();
                offlineIsHost = true;
            }
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
        }

        public void LeaveLobby()
        {
            bool shouldEmitLocalPlayerLeft;
            lock (rpcLock)
            {
                if (!InLobby) return;
                shouldEmitLocalPlayerLeft = perPlayerData.ContainsKey(LocalSteamId);
                InLobby = false;
                HostSteamId64 = LocalSteamId;
                lobbyData.Clear();
                perPlayerData.Clear();
                offlineIsHost = false;
            }
            lock (cryptoStateLock)
            {
                ClearPerPeerSymmetricKeysUnderLock();
                ClearGlobalSharedSecretUnderLock();
                globalHmac?.Dispose();
                globalHmac = null;
            }
            LobbyLeft?.Invoke();

            if (shouldEmitLocalPlayerLeft) PlayerLeft?.Invoke(LocalSteamId);
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

        void ClearGlobalSharedSecretUnderLock()
        {
            if (globalSharedSecret == null) return;
            CryptographicOperations.ZeroMemory(globalSharedSecret);
            globalSharedSecret = null;
        }

        public void InviteToLobby(ulong steamId64)
        {
            if (!IsInitialized)
            {
                LogError("InviteToLobby called before OfflineNetworkingService.Initialize.");
                return;
            }

            if (!InLobby) return;
            if (steamId64 == 0UL)
            {
                LogWarning("InviteToLobby called with invalid target Steam64 id 0.");
                return;
            }
            if (steamId64 != LocalSteamId)
                LogWarning($"Offline mode supports strict local loopback only; invite target {steamId64} is ignored.");
        }

        void EnsureLocalPeerKey()
        {
            lock (cryptoStateLock)
            {
                if (perPeerSymmetricKey.ContainsKey(LocalSteamId)) return;
                using var rng = RandomNumberGenerator.Create();
                var key = new byte[32];
                rng.GetBytes(key);
                perPeerSymmetricKey[LocalSteamId] = key;
            }
        }

        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var t = instance.GetType();
            var registeredHandlers = new List<HandlerRegistration>();
            var token = new RegistrationTransferToken();
            lock (rpcLock)
            {
                foreach (var method in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!method.IsDefined(typeof(CustomRPCAttribute), inherit: false)) continue;
                    if (!rpcs.TryGetValue(modId, out var methods))
                    {
                        methods = new Dictionary<string, List<MessageHandler>>();
                        rpcs[modId] = methods;
                    }
                    if (!methods.TryGetValue(method.Name, out var handlers))
                    {
                        handlers = new List<MessageHandler>();
                        methods[method.Name] = handlers;
                    }
                    var methodParameters = method.GetParameters();
                    if (handlers.Any(existing => existing.Mask == mask && existing.Method == method && ReferenceEquals(existing.Target, instance))) continue;
                    var handler = new MessageHandler
                    {
                        Target = instance,
                        Method = method,
                        Parameters = methodParameters,
                        TakesInfo = methodParameters.Length > 0 && RpcInfoParameterTypeHelper.IsRpcInfoParameterType(methodParameters.Last().ParameterType),
                        Mask = mask
                    };
                    handler.ParameterCountWithoutRpcInfo = handler.TakesInfo ? methodParameters.Length - 1 : methodParameters.Length;
                    handler.OverloadKey = BuildOverloadKey(handler);
                    handlers.Add(handler);
                    registeredHandlers.Add(new HandlerRegistration { MethodName = method.Name, Handler = handler });
                }

                if (registeredHandlers.Count > 0)
                {
                    runtimeRegistrations.Add(new RuntimeRegistration
                    {
                        Instance = instance,
                        ModId = modId,
                        Mask = mask,
                        Token = token
                    });
                }
            }
            token.SetDisposeAction(() =>
            {
                DeregisterHandlers(modId, registeredHandlers);
                RemoveRuntimeRegistration(token);
            });
            return token;
        }
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var registeredHandlers = new List<HandlerRegistration>();
            var token = new RegistrationTransferToken();
            lock (rpcLock)
            {
                var instanceRpc = type
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.IsDefined(typeof(CustomRPCAttribute), inherit: false));
                if (instanceRpc != null) throw new InvalidOperationException($"Cannot register instance RPC method {type.FullName}.{instanceRpc.Name} without an instance.");

                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!method.IsDefined(typeof(CustomRPCAttribute), inherit: false)) continue;

                    if (!rpcs.TryGetValue(modId, out var methods))
                    {
                        methods = new Dictionary<string, List<MessageHandler>>();
                        rpcs[modId] = methods;
                    }
                    if (!methods.TryGetValue(method.Name, out var handlers))
                    {
                        handlers = new List<MessageHandler>();
                        methods[method.Name] = handlers;
                    }
                    var methodParameters = method.GetParameters();
                    if (handlers.Any(existing => existing.Mask == mask && existing.Method == method)) continue;
                    var handler = new MessageHandler
                    {
                        Target = null!,
                        Method = method,
                        Parameters = methodParameters,
                        TakesInfo = methodParameters.Length > 0 && RpcInfoParameterTypeHelper.IsRpcInfoParameterType(methodParameters.Last().ParameterType),
                        Mask = mask
                    };
                    handler.ParameterCountWithoutRpcInfo = handler.TakesInfo ? methodParameters.Length - 1 : methodParameters.Length;
                    handler.OverloadKey = BuildOverloadKey(handler);
                    handlers.Add(handler);
                    registeredHandlers.Add(new HandlerRegistration { MethodName = method.Name, Handler = handler });
                }

                if (registeredHandlers.Count > 0)
                {
                    runtimeRegistrations.Add(new RuntimeRegistration
                    {
                        Type = type,
                        ModId = modId,
                        Mask = mask,
                        Token = token
                    });
                }
            }
            token.SetDisposeAction(() =>
            {
                DeregisterHandlers(modId, registeredHandlers);
                RemoveRuntimeRegistration(token);
            });
            return token;
        }

        void DeregisterHandlers(uint modId, List<HandlerRegistration> handlersToRemove)
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

        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(modId, out var methods)) return;
                foreach (var method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!method.IsDefined(typeof(CustomRPCAttribute), inherit: false)) continue;
                    if (!methods.TryGetValue(method.Name, out var handlers)) continue;
                    for (int i = handlers.Count - 1; i >= 0; i--)
                        if (handlers[i].Target == instance && handlers[i].Mask == mask) handlers.RemoveAt(i);
                    if (handlers.Count == 0) methods.Remove(method.Name);
                }
                if (methods.Count == 0) rpcs.Remove(modId);
                runtimeRegistrations.RemoveAll(registration =>
                    registration.Type == null &&
                    registration.ModId == modId &&
                    registration.Mask == mask &&
                    ReferenceEquals(registration.Instance, instance));
            }
        }
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(modId, out var methods)) return;
                foreach (var methodName in methods.Keys.ToArray())
                {
                    if (!methods.TryGetValue(methodName, out var handlers)) continue;
                    for (int i = handlers.Count - 1; i >= 0; i--)
                        if (handlers[i].Target == null && handlers[i].Method.DeclaringType == type && handlers[i].Mask == mask) handlers.RemoveAt(i);
                    if (handlers.Count == 0) methods.Remove(methodName);
                }
                if (methods.Count == 0) rpcs.Remove(modId);
                runtimeRegistrations.RemoveAll(registration =>
                    registration.Type == type &&
                    registration.ModId == modId &&
                    registration.Mask == mask);
            }
        }

        void RemoveRuntimeRegistration(RegistrationTransferToken token)
        {
            lock (rpcLock)
            {
                runtimeRegistrations.RemoveAll(registration => ReferenceEquals(registration.Token, token));
            }
        }

        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            DispatchIncoming(msg, LocalSteamId);
        }

        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            DispatchIncoming(msg, LocalSteamId);
        }

        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            if (targetSteamId64 == LocalSteamId) DispatchIncoming(msg, LocalSteamId);
        }

        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            if (targetSteamId64 == LocalSteamId) DispatchIncoming(msg, LocalSteamId);
        }

        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby)
            {
                LogError("Not in lobby");
                return;
            }
            RPCTarget(modId, methodName, HostSteamId64, reliable, parameters);
        }

        public void RegisterLobbyDataKey(string key)
        {
            ValidateDataKey(key, nameof(key));
            lock (rpcLock) lobbyKeys.Add(key);
        }
        public void SetLobbyData(string key, object value)
        {
            ValidateDataKey(key, nameof(key));
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            bool unregistered = false, notInLobby = false;
            bool changed = false;
            lock (rpcLock)
            {
                if (InLobby)
                {
                    unregistered = !lobbyKeys.Contains(key);
                    changed = !lobbyData.TryGetValue(key, out var previous) || previous != serialized;
                    if (changed) lobbyData[key] = serialized;
                }
                else notInLobby = true;
            }
            if (notInLobby) { LogError("Cannot set lobby data when not in lobby."); return; }
            if (unregistered) LogWarning($"Accessing unregistered lobby key {key}");
            if (changed) LobbyDataChanged?.Invoke(new[] { key });
        }
        public T GetLobbyData<T>(string key)
        {
            ValidateDataKey(key, nameof(key));
            string? v = null;
            bool found = false, unregistered = false, notInLobby = false;
            lock (rpcLock)
            {
                if (InLobby)
                {
                    unregistered = !lobbyKeys.Contains(key);
                    found = lobbyData.TryGetValue(key, out v);
                }
                else notInLobby = true;
            }
            if (notInLobby) { LogError("Cannot get lobby data when not in lobby."); return default!; }
            if (unregistered) LogWarning($"Accessing unregistered lobby key {key}");
            if (!found) return default!;
            try { return DataValueConverter.ConvertTo<T>(v!); }
            catch (Exception ex) { LogExceptionThrottled($"lobby_parse:{key}", error: true, $"Could not parse lobby data [{key},{v}].", ex); return default!; }
        }

        public void RegisterPlayerDataKey(string key)
        {
            ValidateDataKey(key, nameof(key));
            lock (rpcLock) playerKeys.Add(key);
        }
        public void SetPlayerData(string key, object value)
        {
            ValidateDataKey(key, nameof(key));
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            bool unregistered = false, notInLobby = false;
            bool recreatedLocalBucket = false;
            bool changed = false;
            lock (rpcLock)
            {
                if (InLobby)
                {
                    unregistered = !playerKeys.Contains(key);
                    if (!perPlayerData.TryGetValue(LocalSteamId, out var localData))
                    {
                        localData = new Dictionary<string, string>();
                        perPlayerData[LocalSteamId] = localData;
                        recreatedLocalBucket = true;
                    }
                    changed = !localData.TryGetValue(key, out var previous) || previous != serialized;
                    if (changed) localData[key] = serialized;
                }
                else notInLobby = true;
            }
            if (notInLobby) { LogError("Cannot set player data when not in lobby."); return; }
            if (unregistered) LogWarning($"Accessing unregistered player key {key}");
            if (recreatedLocalBucket) LogWarning($"Local player {LocalSteamId} was missing in per-player state while setting key '{key}'. Recreating local state bucket.");
            if (changed) PlayerDataChanged?.Invoke(LocalSteamId, new[] { key });
        }
        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            ValidateDataKey(key, nameof(key));
            string? v = null;
            bool found = false, unregistered = false, notInLobby = false;
            lock (rpcLock)
            {
                if (InLobby)
                {
                    unregistered = !playerKeys.Contains(key);
                    found = perPlayerData.TryGetValue(steamId64, out var dict) && dict.TryGetValue(key, out v);
                }
                else notInLobby = true;
            }
            if (notInLobby) { LogError("Cannot get player data when not in lobby."); return default!; }
            if (unregistered) LogWarning($"Accessing unregistered player key {key}");
            if (!found) return default!;
            try { return DataValueConverter.ConvertTo<T>(v!); }
            catch (Exception ex) { LogExceptionThrottled($"player_parse:{key}", error: true, $"Could not parse player data [{key},{v}].", ex); return default!; }
        }

        public void PollReceive()
        {
        }

        static void ValidateDataKey(string key, string paramName)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Data key must be a non-empty string.", paramName);
        }

        Message? BuildMessage(uint modId, string methodName, int mask, object?[] parameters, Type[]? parameterTypes)
        {
            try
            {
                MessageHandler[] handlersSnapshot = Array.Empty<MessageHandler>();
                lock (rpcLock)
                {
                    if (rpcs.TryGetValue(modId, out var methods) && methods.TryGetValue(methodName, out var handlers) && handlers.Count > 0)
                        handlersSnapshot = handlers.ToArray();
                }

                if (handlersSnapshot.Length > 0)
                {
                    if (parameterTypes != null && parameterTypes.Length != parameters.Length)
                        throw new ArgumentException($"Parameter type count mismatch: expected {parameterTypes.Length}, got {parameters.Length}", nameof(parameters));

                    MessageHandler? chosen = null;
                    if (parameterTypes != null)
                        chosen = FindTypedHandler(exactMatch: true) ?? FindTypedHandler(exactMatch: false)!;

                    if (chosen == null)
                    {
                        chosen = FindUntypedHandler(allowNullableConversions: false) ?? FindUntypedHandler(allowNullableConversions: true);
                    }

                    if (chosen == null && parameterTypes == null)
                    {
                        chosen = handlersSnapshot.FirstOrDefault(h =>
                        {
                            return h.ParameterCountWithoutRpcInfo == parameters.Length && h.Mask == mask;
                        });
                    }

                    if (chosen == null)
                    {
                        LogError($"No RPC overload matched method '{methodName}' for mask {mask} and parameter list.");
                        return null;
                    }

                    var msg = new Message(modId, methodName, mask, chosen.OverloadKey, messageSizePolicy);
                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.ParameterCountWithoutRpcInfo;
                    if (expectedCountFinal != parameters.Length)
                        throw new ArgumentException($"Parameter count mismatch for {methodName}: expected {expectedCountFinal}, got {parameters.Length}", nameof(parameters));
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
                        if (!IsParameterValueCompatible(t, p))
                            throw new ArgumentException($"Parameter {i} type mismatch: expected {t}, got {p.GetType()}", nameof(parameters));
                        msg.WriteObject(t, p);
                    }

                    if (msg.Length() > messageSizePolicy.MaxLogicalSize)
                    {
                        LogError("Message exceeds maximum allowed overall size.");
                        return null;
                    }

                    return msg;

                    MessageHandler? FindTypedHandler(bool exactMatch)
                    {
                        foreach (var h in handlersSnapshot)
                        {
                            if (h.Mask != mask) continue;
                            var expected = h.Parameters;
                            int expectedCount = h.ParameterCountWithoutRpcInfo;
                            if (expectedCount != parameterTypes!.Length) continue;

                            bool ok = true;
                            for (int i = 0; i < expectedCount; i++)
                            {
                                var t = expected[i].ParameterType;
                                var typed = parameterTypes[i] ?? throw new ArgumentNullException(nameof(parameterTypes), $"Parameter type {i} for {methodName} cannot be null.");
                                if (!IsParameterTypeCompatible(t, typed, exactMatch)) { ok = false; break; }
                                var p = parameters[i];
                                if (p == null)
                                {
                                    if (t.IsValueType && Nullable.GetUnderlyingType(t) == null) { ok = false; break; }
                                    continue;
                                }
                                if (!IsParameterValueCompatible(t, p)) { ok = false; break; }
                            }
                            if (ok) return h;
                        }
                        return null;
                    }

                    MessageHandler? FindUntypedHandler(bool allowNullableConversions)
                    {
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
                                if (allowNullableConversions ? !IsParameterValueCompatible(t, p) : !IsParameterValueCompatibleWithoutNullableFallback(t, p)) { ok = false; break; }
                            }

                            if (ok) return h;
                        }
                        return null;
                    }
                }
                else
                {
                    var msg = new Message(modId, methodName, mask, messageSizePolicy);
                    if (parameterTypes != null)
                    {
                        if (parameterTypes.Length != parameters.Length)
                        {
                            throw new ArgumentException($"Parameter type count mismatch: expected {parameterTypes.Length}, got {parameters.Length}", nameof(parameters));
                        }
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
                            if (!IsParameterValueCompatible(t, p))
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

                    if (msg.Length() > messageSizePolicy.MaxLogicalSize)
                    {
                        LogError("Message exceeds maximum allowed overall size.");
                        return null;
                    }

                    return msg;
                }
            }
            catch (Exception ex) { LogError($"BuildMessage failed: {ex}"); return null; }
        }

        void DispatchIncoming(Message message, ulong from)
        {
            if (IncomingValidator != null && !IncomingValidator(message, from)) return;

            if (!rateLimiter.IncomingAllowed()) return;

            MessageHandler[] handlersSnapshot;
            lock (rpcLock)
            {
                if (!rpcs.TryGetValue(message.ModID, out var methods)) { LogWarning($"No mod {message.ModID}"); return; }
                if (!methods.TryGetValue(message.MethodName, out var handlers)) { LogWarning($"No method {message.MethodName}"); return; }
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

                    if (!TryDeserializeForHandler(message, handler, from, isLocalLoopback: true, out var callParams, out int unread))
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
                LogWarning($"No matching overload for {message.MethodName} (mask {message.Mask})");
                return;
            }

            try { chosenHandler.Method.Invoke(chosenHandler.Target, chosenParams); }
            catch (Exception ex) { LogError($"Invoke RPC error: {ex}"); }
        }

        bool TryDeserializeForHandler(Message source, MessageHandler handler, ulong from, bool isLocalLoopback, out object[] callParams, out int unread)
        {
            callParams = null!;
            unread = int.MaxValue;
            var originalCursor = source.SaveReadCursor();
            var payloadCursor = originalCursor;
            try
            {
                if (originalCursor.Position == 0)
                {
                    payloadCursor = AdvanceCursorPastHeader(source);
                }

                source.RestoreReadCursor(payloadCursor);

                var pi = handler.Parameters;
                int paramCount = handler.ParameterCountWithoutRpcInfo;
                callParams = new object[pi.Length];
                for (int i = 0; i < paramCount; i++) callParams[i] = source.ReadObject(pi[i].ParameterType);
                if (handler.TakesInfo)
                {
                    var t = pi[pi.Length - 1].ParameterType;
                    callParams[pi.Length - 1] = CreateRpcInfoInstance(t, from, isLocalLoopback);
                }
                unread = source.UnreadLength();
                return true;
            }
            catch (Exception ex)
            {
                LogDeserializeFailureThrottled(ex, $"{handler.Method.DeclaringType?.Name ?? "UnknownType"}.{handler.Method.Name}");
                return false;
            }
            finally
            {
                source.RestoreReadCursor(originalCursor);
            }
        }

        static Message.ReadCursor AdvanceCursorPastHeader(Message message)
        {
            message.ReadByte();
            message.ReadUInt();
            message.ReadString();
            message.ReadInt();
            if (message.ProtocolVersion >= 3 && message.ReadBool()) message.ReadString();
            return message.SaveReadCursor();
        }

        object CreateRpcInfoInstance(Type infoType, ulong from, bool isLocalLoopback)
        {
            try
            {
                var ctorFull = infoType.GetConstructor(new[] { typeof(ulong), typeof(string), typeof(bool) });
                if (ctorFull != null) return ctorFull.Invoke(new object[] { from, from.ToString(), isLocalLoopback });
                var ci = infoType.GetConstructor(new[] { typeof(ulong) });
                if (ci != null) return ci.Invoke(new object[] { from });
                var p = Activator.CreateInstance(infoType);
                if (p == null) return null!;

                AssignRpcIdentityMembers(p, infoType, from, isLocalLoopback);
                return p;
            }
            catch (Exception ex)
            {
                LogExceptionThrottled($"rpc_info:{infoType.FullName ?? infoType.Name}", error: false, $"CreateRpcInfoInstance failed for {infoType.FullName ?? infoType.Name}.", ex);
                return null!;
            }
        }

        static void AssignRpcIdentityMembers(object instance, Type infoType, ulong steamId64, bool isLocalLoopback)
        {
            var steamIdString = steamId64.ToString();
            AssignRpcIdentityMember(instance, infoType, "SenderSteamID", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "Sender", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamId64", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamIdString", steamId64, steamIdString);
            AssignRpcLoopbackMember(instance, infoType, isLocalLoopback);
        }

        static void AssignRpcIdentityMember(object instance, Type infoType, string memberName, ulong steamId64, string steamIdString)
        {
            var field = infoType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                TryAssignMemberValue(field.FieldType, value => field.SetValue(instance, value), steamId64, steamIdString);
                return;
            }

            var property = infoType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite) return;
            TryAssignMemberValue(property.PropertyType, value => property.SetValue(instance, value), steamId64, steamIdString);
        }

        static void AssignRpcLoopbackMember(object instance, Type infoType, bool isLocalLoopback)
        {
            var field = infoType.GetField("IsLocalLoopback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                TryAssignLoopbackMemberValue(field.FieldType, value => field.SetValue(instance, value), isLocalLoopback);
                return;
            }

            var property = infoType.GetProperty("IsLocalLoopback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite) return;
            TryAssignLoopbackMemberValue(property.PropertyType, value => property.SetValue(instance, value), isLocalLoopback);
        }

        static void TryAssignLoopbackMemberValue(Type memberType, Action<object> assign, bool isLocalLoopback)
        {
            if (memberType == typeof(bool)) { assign(isLocalLoopback); return; }
            if (memberType == typeof(bool?)) assign((bool?)isLocalLoopback);
        }

        static void TryAssignMemberValue(Type memberType, Action<object> assign, ulong steamId64, string steamIdString)
        {
            var t = Nullable.GetUnderlyingType(memberType) ?? memberType;
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

        static string BuildOverloadKey(MessageHandler handler)
        {
            var pi = handler.Parameters;
            int parameterCount = handler.TakesInfo ? pi.Length - 1 : pi.Length;
            if (parameterCount <= 0) return string.Empty;
            return string.Join("|", pi.Take(parameterCount).Select(p => p.ParameterType.AssemblyQualifiedName ?? p.ParameterType.FullName ?? p.ParameterType.Name));
        }

        static bool IsParameterValueCompatible(Type parameterType, object value)
        {
            var nullableType = Nullable.GetUnderlyingType(parameterType);
            return nullableType != null
                ? nullableType.IsAssignableFrom(value.GetType())
                : parameterType.IsAssignableFrom(value.GetType());
        }

        static bool IsParameterValueCompatibleWithoutNullableFallback(Type parameterType, object value)
        {
            if (Nullable.GetUnderlyingType(parameterType) != null) return false;
            return parameterType.IsAssignableFrom(value.GetType());
        }

        static bool IsParameterTypeCompatible(Type parameterType, Type suppliedType, bool exactMatch)
        {
            if (exactMatch) return parameterType == suppliedType;
            var nullableType = Nullable.GetUnderlyingType(parameterType);
            return parameterType.IsAssignableFrom(suppliedType) || nullableType?.IsAssignableFrom(suppliedType) == true;
        }

        class SlidingWindowRateLimiter
        {
            readonly int limit; readonly TimeSpan window; readonly Queue<DateTime> q = new(); readonly object qLock = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool IncomingAllowed() { lock (qLock) { var now = DateTime.UtcNow; while (q.Count > 0 && now - q.Peek() > window) q.Dequeue(); if (q.Count >= limit) return false; q.Enqueue(now); return true; } }
        }

        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; public int ParameterCountWithoutRpcInfo; public string OverloadKey = string.Empty; }
    }
}
