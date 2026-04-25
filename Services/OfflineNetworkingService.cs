using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using UnityEngine;

namespace NetworkingLibrary.Services
{
    /// <summary>
    /// </summary>
    public class OfflineNetworkingService : INetworkingService
    {
        /// <summary>
        /// </summary>
        public bool IsInitialized { get; private set; }
        /// <summary>
        /// </summary>
        public bool InLobby { get; private set; }
        /// <summary>
        /// </summary>
        public ulong HostSteamId64 { get; private set; } = 1000UL;
        /// <summary>
        /// </summary>
        public string HostIdString => HostSteamId64.ToString();
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

        /// <summary>
        /// Persistent RPC registration map, not tied to lobby lifetime.
        /// </summary>
        readonly Dictionary<uint, Dictionary<string, List<MessageHandler>>> rpcs = new();
        /// <summary>
        /// Per-lobby shared key/value data; cleared whenever lobby identity changes.
        /// </summary>
        readonly Dictionary<string, string> lobbyData = new();
        /// <summary>
        /// Per-lobby player-scoped key/value data for current lobby membership.
        /// </summary>
        readonly Dictionary<ulong, Dictionary<string, string>> perPlayerData = new();
        /// <summary>
        /// Persistent lobby data key registration.
        /// </summary>
        readonly HashSet<string> lobbyKeys = new();
        /// <summary>
        /// Persistent per-player data key registration.
        /// </summary>
        readonly HashSet<string> playerKeys = new();

        readonly SlidingWindowRateLimiter rateLimiter = new(100, TimeSpan.FromSeconds(1));

        readonly Dictionary<ulong, byte[]> perPeerSymmetricKey = new();
        byte[]? globalSharedSecret;
        HMACSHA256? globalHmac;

        public ulong GetLocalSteam64()
        {
            return LocalSteamId;
        }

        public ulong[] GetLobbyMemberSteamIds()
        {
            if (!InLobby) return Array.Empty<ulong>();
            return perPlayerData.Keys.OrderBy(steamId => steamId).ToArray();
        }

        /// <summary>
        /// </summary>
        public void RegisterModSigner(uint modId, Func<byte[], byte[]> signerDelegate) => Net.Logger.LogWarning("This feature is currently not setup in the offline system");
        /// <summary>
        /// </summary>
        public void RegisterModPublicKey(uint modId, RSAParameters pub) => Net.Logger.LogWarning("This feature is currently not setup in the offline system");

        long _nextMessageId = 0;
        private ulong NextMessageId() => (ulong)System.Threading.Interlocked.Increment(ref _nextMessageId);

        /// <summary>
        /// </summary>
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

        bool offlineIsHost = false;
        public bool IsHost => offlineIsHost;

        /// <summary>
        /// </summary>
        public void Initialize()
        {
            if (IsInitialized) return;
            using var rng = RandomNumberGenerator.Create();
            var k = new byte[32];
            rng.GetBytes(k);
            perPeerSymmetricKey[LocalSteamId] = k;
            IsInitialized = true;
        }

        /// <summary>
        /// </summary>
        public void Shutdown()
        {
            IsInitialized = false;
            InLobby = false;
            offlineIsHost = false;
            HostSteamId64 = LocalSteamId;
            lobbyData.Clear();
            perPlayerData.Clear();
            ClearPerPeerSymmetricKeys();
            ClearGlobalSharedSecret();
            globalHmac?.Dispose(); globalHmac = null;
        }

        /// <summary>
        /// </summary>
        public void CreateLobby(int maxPlayers = 8)
        {
            InLobby = true;
            HostSteamId64 = LocalSteamId;
            lobbyData.Clear();
            perPlayerData.Clear();
            perPlayerData[LocalSteamId] = new Dictionary<string, string>();
            // Event contract (deterministic): LobbyCreated (host-only) -> LobbyEntered -> PlayerEntered(local member).
            LobbyCreated?.Invoke();
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
            offlineIsHost = true;
        }

        /// <summary>
        /// </summary>
        public void JoinLobby(ulong lobbySteamId64)
        {
            InLobby = true;
            HostSteamId64 = lobbySteamId64;
            lobbyData.Clear();
            perPlayerData.Clear();
            perPlayerData[LocalSteamId] = new Dictionary<string, string>();
            // Event contract (deterministic): LobbyEntered -> PlayerEntered(local member).
            LobbyEntered?.Invoke();
            PlayerEntered?.Invoke(LocalSteamId);
            offlineIsHost = false;
        }

        /// <summary>
        /// </summary>
        public void LeaveLobby()
        {
            InLobby = false;
            HostSteamId64 = LocalSteamId;
            lobbyData.Clear();
            perPlayerData.Clear();
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

        /// <summary>
        /// </summary>
        public void InviteToLobby(ulong steamId64)
        {
            if (!InLobby) return;
            if (!perPlayerData.ContainsKey(steamId64)) perPlayerData[steamId64] = new Dictionary<string, string>();
            PlayerEntered?.Invoke(steamId64);
        }

        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var t = instance.GetType();
            int registered = 0;
            foreach (var method in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attrs = method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().ToArray();
                if (attrs.Length == 0) continue;
                if (!rpcs.ContainsKey(modId)) rpcs[modId] = new Dictionary<string, List<MessageHandler>>();
                if (!rpcs[modId].ContainsKey(method.Name)) rpcs[modId][method.Name] = new List<MessageHandler>();
                rpcs[modId][method.Name].Add(new MessageHandler
                {
                    Target = instance,
                    Method = method,
                    Parameters = method.GetParameters(),
                    TakesInfo = method.GetParameters().Length > 0 && method.GetParameters().Last().ParameterType.Name == "RPCInfo",
                    Mask = mask
                });
                registered++;
            }
            return new Token(() => DeregisterNetworkObject(instance, modId, mask));
        }
        /// <summary>
        /// </summary>
        public IDisposable RegisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attrs = method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().ToArray();
                if (attrs.Length == 0) continue;
                if (!method.IsStatic) throw new InvalidOperationException($"Cannot register instance RPC method {type.FullName}.{method.Name} without an instance.");

                if (!rpcs.ContainsKey(modId)) rpcs[modId] = new Dictionary<string, List<MessageHandler>>();
                if (!rpcs[modId].ContainsKey(method.Name)) rpcs[modId][method.Name] = new List<MessageHandler>();
                rpcs[modId][method.Name].Add(new MessageHandler
                {
                    Target = null!,
                    Method = method,
                    Parameters = method.GetParameters(),
                    TakesInfo = method.GetParameters().Length > 0 && method.GetParameters().Last().ParameterType.Name == "RPCInfo",
                    Mask = mask
                });
            }
            return new Token(() => DeregisterNetworkType(type, modId, mask));
        }

        /// <summary>
        /// </summary>
        public void DeregisterNetworkObject(object instance, uint modId, int mask = 0)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (!rpcs.TryGetValue(modId, out var methods)) return;
            foreach (var method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attrs = method.GetCustomAttributes(false).OfType<CustomRPCAttribute>().ToArray();
                if (attrs.Length == 0) continue;
                if (!methods.TryGetValue(method.Name, out var handlers)) continue;
                for (int i = handlers.Count - 1; i >= 0; i--)
                    if (handlers[i].Target == instance && handlers[i].Mask == mask) handlers.RemoveAt(i);
            }
        }
        /// <summary>
        /// </summary>
        public void DeregisterNetworkType(Type type, uint modId, int mask = 0)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
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

        /// <summary>
        /// </summary>
        public void RPC(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            DispatchIncoming(msg, LocalSteamId);
        }

        public void RPC(uint modId, string methodName, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("RPC called while not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            DispatchIncoming(msg, LocalSteamId);
        }

        /// <summary>
        /// </summary>
        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, null);
            if (msg == null) return;
            if (targetSteamId64 == LocalSteamId) DispatchIncoming(msg, LocalSteamId);
        }

        public void RPCTarget(uint modId, string methodName, ulong targetSteamId64, ReliableType reliable, Type[] parameterTypes, params object?[] parameters)
        {
            if (!InLobby) { Net.Logger.LogError("Cannot RPC target when not in lobby"); return; }
            var msg = BuildMessage(modId, methodName, 0, parameters, parameterTypes);
            if (msg == null) return;
            if (targetSteamId64 == LocalSteamId) DispatchIncoming(msg, LocalSteamId);
        }

        /// <summary>
        /// </summary>
        public void RPCToHost(uint modId, string methodName, ReliableType reliable, params object[] parameters)
        {
            if (!InLobby)
            {
                Net.Logger.LogError("Not in lobby");
                return;
            }
            RPCTarget(modId, methodName, HostSteamId64, reliable, parameters);
        }

        /// <summary>
        /// </summary>
        public void RegisterLobbyDataKey(string key) => lobbyKeys.Add(key);
        /// <summary>
        /// </summary>
        public void SetLobbyData(string key, object value)
        {
            if (!InLobby) { Debug.LogError("Cannot set lobby data when not in lobby."); return; }
            if (!lobbyKeys.Contains(key)) Debug.LogWarning($"Accessing unregistered lobby key {key}");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            lobbyData[key] = serialized;
            LobbyDataChanged?.Invoke(new[] { key });
        }
        /// <summary>
        /// </summary>
        public T GetLobbyData<T>(string key)
        {
            if (!InLobby) { Debug.LogError("Cannot get lobby data when not in lobby."); return default!; }
            if (!lobbyKeys.Contains(key)) Debug.LogWarning($"Accessing unregistered lobby key {key}");
            if (!lobbyData.TryGetValue(key, out var v)) return default!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Debug.LogError($"Could not parse lobby data [{key},{v}]"); return default!; }
        }

        /// <summary>
        /// </summary>
        public void RegisterPlayerDataKey(string key) => playerKeys.Add(key);
        /// <summary>
        /// </summary>
        public void SetPlayerData(string key, object value)
        {
            if (!InLobby) { Debug.LogError("Cannot set player data when not in lobby."); return; }
            if (!playerKeys.Contains(key)) Debug.LogWarning($"Accessing unregistered player key {key}");
            var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            perPlayerData[LocalSteamId][key] = serialized;
            PlayerDataChanged?.Invoke(LocalSteamId, new[] { key });
        }
        /// <summary>
        /// </summary>
        public T GetPlayerData<T>(ulong steamId64, string key)
        {
            if (!InLobby) { Debug.LogError("Cannot get player data when not in lobby."); return default!; }
            if (!playerKeys.Contains(key)) Debug.LogWarning($"Accessing unregistered player key {key}");
            if (!perPlayerData.TryGetValue(steamId64, out var dict)) return default!;
            if (!dict.TryGetValue(key, out var v)) return default!;
            try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { Debug.LogError($"Could not parse player data [{key},{v}]"); return default!; }
        }

        /// <summary>
        /// </summary>
        public void PollReceive()
        {
            // Nothing queued in offline mode.
        }

        Message? BuildMessage(uint modId, string methodName, int mask, object?[] parameters, Type[]? parameterTypes)
        {
            try
            {
                if (rpcs.TryGetValue(modId, out var methods) && methods.TryGetValue(methodName, out var handlers) && handlers.Count > 0)
                {
                    MessageHandler chosen = null!;
                    foreach (var h in handlers)
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
                        chosen = handlers.FirstOrDefault(h =>
                        {
                            int expectedCount = h.TakesInfo ? h.Parameters.Length - 1 : h.Parameters.Length;
                            return expectedCount == parameters.Length && h.Mask == mask;
                        });
                    }

                    if (chosen == null)
                    {
                        Debug.LogError($"No RPC overload matched method '{methodName}' for mask {mask} and parameter list.");
                        return null;
                    }

                    var msg = new Message(modId, methodName, mask, BuildOverloadKey(chosen));
                    var expectedParams = chosen.Parameters;
                    int expectedCountFinal = chosen.TakesInfo ? expectedParams.Length - 1 : expectedParams.Length;
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
                    return msg;
                }
                else
                {
                    var msg = new Message(modId, methodName, mask);
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
                    return msg;
                }
            }
            catch (Exception ex) { Debug.LogError($"BuildMessage failed: {ex}"); return null; }
        }

        void DispatchIncoming(Message message, ulong from)
        {
            if (IncomingValidator != null && !IncomingValidator(message, from)) return;

            if (!rateLimiter.IncomingAllowed()) return;

            if (!rpcs.TryGetValue(message.ModID, out var methods)) { Debug.LogWarning($"No mod {message.ModID}"); return; }
            if (!methods.TryGetValue(message.MethodName, out var handlers)) { Debug.LogWarning($"No method {message.MethodName}"); return; }

            MessageHandler? chosenHandler = null;
            object[]? chosenParams = null;
            MessageHandler? fallbackHandler = null;
            object[]? fallbackParams = null;

            IEnumerable<MessageHandler> dispatchOrder = handlers.Where(h => h.Mask == message.Mask);
            if (!string.IsNullOrEmpty(message.OverloadKey))
            {
                var keyed = dispatchOrder.Where(h => BuildOverloadKey(h) == message.OverloadKey).ToArray();
                if (keyed.Length > 0) dispatchOrder = keyed.Concat(dispatchOrder.Where(h => BuildOverloadKey(h) != message.OverloadKey));
            }

            foreach (var handler in dispatchOrder)
            {
                if (!TryDeserializeForHandler(message, handler, from, out var callParams, out int unread))
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
                Debug.LogWarning($"No matching overload for {message.MethodName} (mask {message.Mask})");
                return;
            }

            try { chosenHandler.Method.Invoke(chosenHandler.Target, chosenParams); }
            catch (Exception ex) { Debug.LogError($"Invoke RPC error: {ex}"); }
        }

        bool TryDeserializeForHandler(Message source, MessageHandler handler, ulong from, out object[] callParams, out int unread)
        {
            callParams = null!;
            unread = int.MaxValue;
            try
            {
                var msgCopy = new Message(source.ToArray());
                var pi = handler.Parameters;
                int paramCount = handler.TakesInfo ? pi.Length - 1 : pi.Length;
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
                var field = infoType.GetField("SenderSteamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) field.SetValue(p, from);
                return p!;
            }
            catch { return null!; }
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
            readonly int limit; readonly TimeSpan window; readonly Queue<DateTime> q = new();
            public SlidingWindowRateLimiter(int limit, TimeSpan window) { this.limit = limit; this.window = window; }
            public bool IncomingAllowed() { var now = DateTime.UtcNow; while (q.Count > 0 && now - q.Peek() > window) q.Dequeue(); if (q.Count >= limit) return false; q.Enqueue(now); return true; }
        }

        class MessageHandler { public object Target = null!; public MethodInfo Method = null!; public ParameterInfo[] Parameters = null!; public bool TakesInfo; public int Mask; }
    }
}
