using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetworkingLibrary.Modules;

namespace NetworkingLibrary.Services
{
    public class OfflineNetworkingService : INetworkingService
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

        static void LogError(string message)
        {
            try { Net.Logger?.LogError(message); } catch { }
        }

        static void LogWarning(string message)
        {
            try { Net.Logger?.LogWarning(message); } catch { }
        }

        public ulong GetLocalSteam64()
        {
            return LocalSteamId;
        }

        public ulong[] GetLobbyMemberSteamIds()
        {
            if (!InLobby) return Array.Empty<ulong>();
            return perPlayerData.Keys.OrderBy(steamId => steamId).ToArray();
        }

        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate)
        {
            if (signerDelegate == null) throw new ArgumentNullException(nameof(signerDelegate));
            modSigners[modId] = signerDelegate;
        }
        public void RegisterModPublicKey(uint modId, RSAParameters pub)
        {
            if (pub.Modulus == null || pub.Modulus.Length == 0)
                throw new ArgumentException("RSA public key modulus must not be empty.", nameof(pub));
            if (pub.Exponent == null || pub.Exponent.Length == 0)
                throw new ArgumentException("RSA public key exponent must not be empty.", nameof(pub));
            modPublicKeys[modId] = pub;
        }

        long _nextMessageId = 0;
        private ulong NextMessageId() => (ulong)System.Threading.Interlocked.Increment(ref _nextMessageId);

        public ulong LocalSteamId { get; private set; } = 1000;

        class Token : IDisposable
        {
            Action? on;
            public Token(Action on) { this.on = on; }
            public void Dispose()
            {
                var action = on;
                if (action == null) return;
                on = null;
                action();
            }
        }

        sealed class HandlerRegistration
        {
            public string MethodName = string.Empty;
            public MessageHandler Handler = null!;
        }

        bool offlineIsHost = false;
        int offlineLobbyCapacity = 8;
        public bool IsHost => offlineIsHost;

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

            IsInitialized = false;
            offlineIsHost = false;
            offlineLobbyCapacity = 8;
            HostSteamId64 = LocalSteamId;
            lock (rpcLock)
            {
                rpcs.Clear();
            }
            lobbyKeys.Clear();
            playerKeys.Clear();
            lobbyData.Clear();
            perPlayerData.Clear();
            ClearPerPeerSymmetricKeys();
            ClearGlobalSharedSecret();
            modSigners.Clear();
            modPublicKeys.Clear();
            globalHmac?.Dispose(); globalHmac = null;
        }

        public void CreateLobby(int maxPlayers = 8)
        {
            if (!IsInitialized)
            {
                LogError("CreateLobby called before OfflineNetworkingService.Initialize.");
                return;
            }
            if (maxPlayers <= 0)
            {
                LogWarning($"CreateLobby maxPlayers {maxPlayers} is invalid. Clamping to 1.");
                maxPlayers = 1;
            }
            if (InLobby)
            {
                LogWarning("CreateLobby called while already in a lobby. Leaving current lobby before creating a new one.");
                LeaveLobby();
            }

            EnsureLocalPeerKey();
            offlineLobbyCapacity = maxPlayers;
            InLobby = true;
            HostSteamId64 = LocalSteamId;
            lobbyData.Clear();
            perPlayerData.Clear();
            perPlayerData[LocalSteamId] = new Dictionary<string, string>();
            LobbyCreated?.Invoke();
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
            offlineIsHost = true;
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
            offlineLobbyCapacity = 8;
            InLobby = true;
            if (lobbySteamId64 != LocalSteamId)
                LogWarning($"Offline single-peer host simulation uses local peer {LocalSteamId} as host identity; lobby id {lobbySteamId64} is compatibility-only.");
            HostSteamId64 = LocalSteamId;
            lobbyData.Clear();
            perPlayerData.Clear();
            perPlayerData[LocalSteamId] = new Dictionary<string, string>();
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
            offlineIsHost = true;
        }

        public void LeaveLobby()
        {
            if (!InLobby) return;

            InLobby = false;
            HostSteamId64 = LocalSteamId;
            offlineLobbyCapacity = 8;
            lobbyData.Clear();
            perPlayerData.Clear();
            lobbyKeys.Clear();
            playerKeys.Clear();
            ClearPerPeerSymmetricKeys();
            ClearGlobalSharedSecret();
            globalHmac?.Dispose(); globalHmac = null;
            LobbyLeft?.Invoke();
            offlineIsHost = false;
        }

        void ClearPerPeerSymmetricKeys()
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
            {
                LogWarning($"Offline mode supports strict local loopback only; invite target {steamId64} is ignored and lobby capacity checks are bypassed.");
                return;
            }
            if (perPlayerData.ContainsKey(steamId64)) return;
            if (perPlayerData.Count >= offlineLobbyCapacity)
            {
                LogWarning($"Offline lobby is at capacity ({offlineLobbyCapacity}); cannot add player {steamId64}.");
                return;
            }
            perPlayerData[steamId64] = new Dictionary<string, string>();
            PlayerEntered?.Invoke(steamId64);
        }

        void EnsureLocalPeerKey()
        {
            if (perPeerSymmetricKey.ContainsKey(LocalSteamId)) return;
            using var rng = RandomNumberGenerator.Create();
            var key = new byte[32];
            rng.GetBytes(key);
            perPeerSymmetricKey[LocalSteamId] = key;
        }

        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var t = instance.GetType();
            var registeredHandlers = new List<HandlerRegistration>();
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
            }
            return new Token(() => DeregisterHandlers(modId, registeredHandlers));
        }
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var registeredHandlers = new List<HandlerRegistration>();
            lock (rpcLock)
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!method.IsDefined(typeof(CustomRPCAttribute), inherit: false)) continue;
                    if (!method.IsStatic) throw new InvalidOperationException($"Cannot register instance RPC method {type.FullName}.{method.Name} without an instance.");

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
            }
            return new Token(() => DeregisterHandlers(modId, registeredHandlers));
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
                        if (handlers[i].Method.DeclaringType == type && handlers[i].Mask == mask) handlers.RemoveAt(i);
                    if (handlers.Count == 0) methods.Remove(methodName);
                }
                if (methods.Count == 0) rpcs.Remove(modId);
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
            lobbyKeys.Add(key);
        }
        public void SetLobbyData(string key, object value)
        {
            ValidateDataKey(key, nameof(key));
            if (!InLobby) { LogError("Cannot set lobby data when not in lobby."); return; }
            if (!lobbyKeys.Contains(key)) LogWarning($"Accessing unregistered lobby key {key}");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            lobbyData[key] = serialized;
            LobbyDataChanged?.Invoke(new[] { key });
        }
        public T GetLobbyData<T>(string key)
        {
            ValidateDataKey(key, nameof(key));
            if (!InLobby) { LogError("Cannot get lobby data when not in lobby."); return default!; }
            if (!lobbyKeys.Contains(key)) LogWarning($"Accessing unregistered lobby key {key}");
            if (!lobbyData.TryGetValue(key, out var v)) return default!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { LogError($"Could not parse lobby data [{key},{v}]"); return default!; }
        }

        public void RegisterPlayerDataKey(string key)
        {
            ValidateDataKey(key, nameof(key));
            playerKeys.Add(key);
        }
        public void SetPlayerData(string key, object value)
        {
            ValidateDataKey(key, nameof(key));
            if (!InLobby) { LogError("Cannot set player data when not in lobby."); return; }
            if (!playerKeys.Contains(key)) LogWarning($"Accessing unregistered player key {key}");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!perPlayerData.ContainsKey(LocalSteamId))
            {
                LogWarning($"Local player {LocalSteamId} was missing in per-player state while setting key '{key}'. Recreating local state bucket.");
                perPlayerData[LocalSteamId] = new Dictionary<string, string>();
            }
            perPlayerData[LocalSteamId][key] = serialized;
            PlayerDataChanged?.Invoke(LocalSteamId, new[] { key });
        }
        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            ValidateDataKey(key, nameof(key));
            if (!InLobby) { LogError("Cannot get player data when not in lobby."); return default!; }
            if (!playerKeys.Contains(key)) LogWarning($"Accessing unregistered player key {key}");
            if (!perPlayerData.TryGetValue(steamId64, out var dict)) return default!;
            if (!dict.TryGetValue(key, out var v)) return default!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { LogError($"Could not parse player data [{key},{v}]"); return default!; }
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
                        LogError($"No RPC overload matched method '{methodName}' for mask {mask} and parameter list.");
                        return null;
                    }

                    var msg = new Message(modId, methodName, mask, chosen.OverloadKey, messageSizePolicy);
                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.ParameterCountWithoutRpcInfo;
                    if (expectedCountFinal != parameters.Length)
                        throw new Exception($"Parameter count mismatch for {methodName}: expected {expectedCountFinal}, got {parameters.Length}");
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

                    if (msg.Length() > messageSizePolicy.MaxLogicalSize)
                    {
                        LogError("Message exceeds maximum allowed overall size.");
                        return null;
                    }

                    return msg;
                }
                else
                {
                    var msg = new Message(modId, methodName, mask, messageSizePolicy);
                    if (parameterTypes != null)
                    {
                        if (parameterTypes.Length != parameters.Length)
                        {
                            throw new Exception($"Parameter type count mismatch: expected {parameterTypes.Length}, got {parameters.Length}");
                        }
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

                    if (!TryDeserializeForHandler(message, handler, from, out var callParams, out int unread))
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

        bool TryDeserializeForHandler(Message source, MessageHandler handler, ulong from, out object[] callParams, out int unread)
        {
            callParams = null!;
            unread = int.MaxValue;
            try
            {
                var msgCopy = new Message(source.ToArray(), messageSizePolicy);
                var pi = handler.Parameters;
                int paramCount = handler.ParameterCountWithoutRpcInfo;
                callParams = new object[pi.Length];
                for (int i = 0; i < paramCount; i++) callParams[i] = msgCopy.ReadObject(pi[i].ParameterType);
                if (handler.TakesInfo)
                {
                    var t = pi[pi.Length - 1].ParameterType;
                    callParams[pi.Length - 1] = CreateRpcInfoInstance(t, from);
                }
                unread = msgCopy.UnreadLength();
                return true;
            }
            catch
            {
                return false;
            }
        }

        object CreateRpcInfoInstance(Type infoType, ulong from)
        {
            try
            {
                var ci = infoType.GetConstructor(new[] { typeof(ulong) });
                if (ci != null) return ci.Invoke(new object[] { from });
                var p = Activator.CreateInstance(infoType);
                if (p == null) return null!;

                AssignRpcIdentityMembers(p, infoType, from);
                return p;
            }
            catch { return null!; }
        }

        static void AssignRpcIdentityMembers(object instance, Type infoType, ulong steamId64)
        {
            var steamIdString = steamId64.ToString();
            AssignRpcIdentityMember(instance, infoType, "SenderSteamID", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "Sender", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamId64", steamId64, steamIdString);
            AssignRpcIdentityMember(instance, infoType, "SteamIdString", steamId64, steamIdString);
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

        class SlidingWindowRateLimiter
        {
            readonly int limit; readonly TimeSpan window; readonly Queue<DateTime> q = new(); readonly object qLock = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool IncomingAllowed() { lock (qLock) { var now = DateTime.UtcNow; while (q.Count > 0 && now - q.Peek() > window) q.Dequeue(); if (q.Count >= limit) return false; q.Enqueue(now); return true; } }
        }

        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; public int ParameterCountWithoutRpcInfo; public string OverloadKey = string.Empty; }
    }
}
