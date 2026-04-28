using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using NetworkingLibrary.Services;
using UnityEngine;
using PhotonPlayer = Photon.Realtime.Player;

namespace NetworkingLibrary.Modules
{
    public static class NetworkingPhotonExtensions
    {
        static readonly TimeSpan UnresolvedMappingWarningCooldown = TimeSpan.FromSeconds(7);
        static readonly TimeSpan UnresolvedMappingWarningPruneAge = TimeSpan.FromSeconds(UnresolvedMappingWarningCooldown.TotalSeconds * 4);
        const int UnresolvedMappingWarningThrottleMaxEntries = 2048;
        static readonly Dictionary<string, DateTime> UnresolvedMappingWarningThrottle = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        static readonly object UnresolvedMappingWarningThrottleLock = new object();
        static DateTime UnresolvedMappingWarningNextPruneUtc = DateTime.MinValue;
        static readonly TimeSpan PersonaLookupExceptionCooldown = TimeSpan.FromSeconds(5);
        static DateTime lastPersonaLookupExceptionUtc = DateTime.MinValue;
        static int suppressedPersonaLookupExceptions;
        static readonly object personaLookupExceptionLock = new object();
        static readonly TimeSpan PersonaNameCacheTtl = TimeSpan.FromSeconds(5);
        static readonly TimeSpan PersonaNameCachePruneAge = TimeSpan.FromSeconds(PersonaNameCacheTtl.TotalSeconds * 6);
        const int PersonaNameCacheMaxEntries = 4096;
        static readonly Dictionary<ulong, PersonaNameCacheEntry> PersonaNameCache = new Dictionary<ulong, PersonaNameCacheEntry>();
        static readonly object PersonaNameCacheLock = new object();
        static DateTime PersonaNameCacheNextPruneUtc = DateTime.MinValue;
        const ulong SteamId64Base = 76561197960265728UL;
        internal static Func<bool> PhotonInRoom = () => PhotonNetwork.InRoom;
        internal static Func<Room?> CurrentPhotonRoom = () => PhotonNetwork.CurrentRoom;
        internal static Func<ulong, string> PersonaNameLookup = sid => SteamFriends.GetFriendPersonaName(new CSteamID(sid));
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;
        static readonly string[] StableIdPropertyKeys =
        {
            "steam64", "steamid64", "steam_id64", "steamid", "steam_id", "steam", "authid", "auth_id", "userid", "user_id"
        };

        struct PersonaNameCacheEntry
        {
            public string Name;
            public DateTime UpdatedUtc;
        }

        public static Dictionary<int, ulong> MapPhotonActorsToSteam(INetworkingService svc)
        {
            var map = new Dictionary<int, ulong>();
            if (svc == null) return map;

            if (!TryGetCurrentPhotonRoom(out var room)) return map;
            var lobbyIds = svc.GetLobbyMemberSteamIds();
            if (lobbyIds == null || lobbyIds.Length == 0) return map;

            var lobbyIdSet = new HashSet<ulong>(lobbyIds);
            var personaByName = BuildPersonaLookupIndex(lobbyIds);

            foreach (var kv in room!.Players)
            {
                int actor = kv.Key;
                var player = kv.Value;

                if (TryResolveStableIdentity(player, lobbyIdSet, out var stableMatch, out var stableIssue))
                {
                    map[actor] = stableMatch;
                    continue;
                }

                if (!string.IsNullOrEmpty(player.NickName) && personaByName.TryGetValue(player.NickName, out var nicknameMatches))
                {
                    if (nicknameMatches.Count == 1)
                    {
                        map[actor] = nicknameMatches[0];
                    }
                    else
                    {
                        LogUnresolvedMappingWarning(actor, $"ambiguous nickname '{player.NickName}' across {nicknameMatches.Count} lobby members", $"Photon actor {actor} nickname '{player.NickName}' is ambiguous across {nicknameMatches.Count} lobby members; actor left unmapped.");
                    }
                }
                else
                {
                    var issue = stableIssue ?? "no stable identity present and no nickname match found";
                    LogUnresolvedMappingWarning(actor, issue, $"Photon actor {actor} has no Steam mapping ({issue}); actor left unmapped.");
                }
            }

            return map;
        }

        static bool TryGetCurrentPhotonRoom(out Room? room)
        {
            room = null;
            try
            {
                if (!PhotonInRoom()) return false;
                room = CurrentPhotonRoom();
                return room != null;
            }
            catch (Exception ex)
            {
                LogWarning($"Photon room state unavailable while mapping actors; actor map left empty. Exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        static bool TryResolveStableIdentity(PhotonPlayer player, HashSet<ulong> lobbyIdSet, out ulong matchedSteamId, out string? issue)
        {
            matchedSteamId = 0;
            issue = null;

            var candidates = new HashSet<ulong>();
            foreach (var candidate in GetStableIdCandidates(player))
            {
                if (!lobbyIdSet.Contains(candidate)) continue;
                candidates.Add(candidate);
            }

            if (candidates.Count == 1)
            {
                foreach (var candidate in candidates)
                {
                    matchedSteamId = candidate;
                    return true;
                }
            }

            if (candidates.Count > 1)
            {
                var sortedCandidates = new List<ulong>(candidates);
                sortedCandidates.Sort();
                issue = $"multiple stable IDs matched lobby members ({string.Join(",", sortedCandidates)})";
                LogUnresolvedMappingWarning(player.ActorNumber, issue, $"Photon actor {player.ActorNumber} has ambiguous stable identity: {issue}.");
                return false;
            }

            issue = "no stable ID candidate matched lobby members";
            return false;
        }

        static IEnumerable<ulong> GetStableIdCandidates(PhotonPlayer player)
        {
            if (player == null) yield break;

            if (TryParseSteamId(player.UserId, out var userIdSteam)) yield return userIdSteam;

            if (player.CustomProperties == null || player.CustomProperties.Count == 0) yield break;

            foreach (DictionaryEntry entry in player.CustomProperties)
            {
                if (!(entry.Key is string key) || !LooksLikeStableIdKey(key)) continue;
                if (TryParseSteamId(entry.Value, out var valueSteam)) yield return valueSteam;
            }
        }

        static bool LooksLikeStableIdKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < StableIdPropertyKeys.Length; i++)
            {
                if (key.Equals(StableIdPropertyKeys[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool TryParseSteamId(object raw, out ulong steamId)
        {
            steamId = 0;
            if (raw == null) return false;

            switch (raw)
            {
                case CSteamID sid when IsPlausibleSteam64(sid.m_SteamID):
                    steamId = sid.m_SteamID;
                    return true;
                case ulong u when IsPlausibleSteam64(u):
                    steamId = u;
                    return true;
                case long l when l > 0 && IsPlausibleSteam64((ulong)l):
                    steamId = (ulong)l;
                    return true;
                case string s when TryParseSteamIdString(s, out var parsedString):
                    steamId = parsedString;
                    return true;
                default:
                    return false;
            }
        }

        static bool TryParseSteamIdString(string raw, out ulong steamId)
        {
            steamId = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var value = raw.Trim();
            if (ulong.TryParse(value, out var parsed) && IsPlausibleSteam64(parsed))
            {
                steamId = parsed;
                return true;
            }

            if (value.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Substring(6).Split(':');
                if (parts.Length != 3) return false;
                if (!int.TryParse(parts[0], out var universe) || (universe != 0 && universe != 1)) return false;
                if (!int.TryParse(parts[1], out var authServer) || (authServer != 0 && authServer != 1)) return false;
                if (!ulong.TryParse(parts[2], out var accountNumber)) return false;
                if (accountNumber > (ulong.MaxValue - (ulong)authServer) / 2UL) return false;
                return TryComposePublicSteam64(accountNumber * 2UL + (ulong)authServer, out steamId);
            }

            if (value.Length > 2 && value[0] == '[' && value[value.Length - 1] == ']') value = value.Substring(1, value.Length - 2);
            var steam3Parts = value.Split(':');
            if (steam3Parts.Length != 3 || !steam3Parts[0].Equals("U", StringComparison.OrdinalIgnoreCase)) return false;
            if (!int.TryParse(steam3Parts[1], out var steam3Universe) || steam3Universe != 1) return false;
            if (!ulong.TryParse(steam3Parts[2], out var accountId)) return false;
            return TryComposePublicSteam64(accountId, out steamId);
        }

        static bool TryComposePublicSteam64(ulong accountId, out ulong steamId)
        {
            steamId = 0;
            if (accountId > ulong.MaxValue - SteamId64Base) return false;
            steamId = SteamId64Base + accountId;
            return IsPlausibleSteam64(steamId);
        }

        static bool IsPlausibleSteam64(ulong value) => value >= SteamId64Base;

        internal static bool TryParseSteamIdForTests(object raw, out ulong steamId)
        {
            return TryParseSteamId(raw, out steamId);
        }

        static Dictionary<string, List<ulong>> BuildPersonaLookupIndex(ulong[] lobbyIds)
        {
            var personaByName = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
            if (lobbyIds == null || lobbyIds.Length == 0) return personaByName;

            var seenLobbyIds = new HashSet<ulong>();
            for (int i = 0; i < lobbyIds.Length; i++)
            {
                var sid = lobbyIds[i];
                if (!IsPlausibleSteam64(sid)) continue;
                if (!seenLobbyIds.Add(sid)) continue;
                if (!TryGetPersonaName(sid, out var name) || string.IsNullOrEmpty(name)) continue;
                if (!personaByName.TryGetValue(name, out var ids)) personaByName[name] = ids = new List<ulong>(1);
                ids.Add(sid);
            }

            return personaByName;
        }

        static bool TryGetPersonaName(ulong sid, out string? name)
        {
            var now = UtcNow();
            lock (PersonaNameCacheLock)
            {
                if (now >= PersonaNameCacheNextPruneUtc)
                {
                    PrunePersonaNameCache(now);
                    PersonaNameCacheNextPruneUtc = now + PersonaNameCacheTtl;
                }

                if (PersonaNameCache.TryGetValue(sid, out var cached)
                    && now >= cached.UpdatedUtc
                    && now - cached.UpdatedUtc < PersonaNameCacheTtl)
                {
                    name = cached.Name;
                    return true;
                }
            }

            try
            {
                name = PersonaNameLookup(sid) ?? string.Empty;
            }
            catch (Exception ex)
            {
                LogPersonaLookupExceptionThrottled(ex, sid);
                name = string.Empty;
                return false;
            }

            lock (PersonaNameCacheLock)
            {
                PersonaNameCache[sid] = new PersonaNameCacheEntry
                {
                    Name = name,
                    UpdatedUtc = now
                };
                CapPersonaNameCache();
            }

            return true;
        }

        static void PrunePersonaNameCache(DateTime nowUtc)
        {
            if (PersonaNameCache.Count == 0) return;
            var expiresBeforeUtc = nowUtc - PersonaNameCachePruneAge;
            List<ulong>? keysToRemove = null;
            foreach (var entry in PersonaNameCache)
            {
                if (entry.Value.UpdatedUtc >= expiresBeforeUtc) continue;
                if (keysToRemove == null) keysToRemove = new List<ulong>();
                keysToRemove.Add(entry.Key);
            }

            if (keysToRemove == null) return;
            for (int i = 0; i < keysToRemove.Count; i++) PersonaNameCache.Remove(keysToRemove[i]);
        }

        static void CapPersonaNameCache()
        {
            if (PersonaNameCache.Count <= PersonaNameCacheMaxEntries) return;

            ulong oldestKey = 0;
            var oldestTime = DateTime.MaxValue;
            foreach (var entry in PersonaNameCache)
            {
                if (entry.Value.UpdatedUtc >= oldestTime) continue;
                oldestTime = entry.Value.UpdatedUtc;
                oldestKey = entry.Key;
            }

            if (oldestKey != 0) PersonaNameCache.Remove(oldestKey);
        }

        internal static Dictionary<string, List<ulong>> BuildPersonaLookupIndexForTests(ulong[] lobbyIds)
        {
            return BuildPersonaLookupIndex(lobbyIds);
        }

        internal static void ResetPersonaNameCacheForTests()
        {
            lock (PersonaNameCacheLock)
            {
                PersonaNameCache.Clear();
                PersonaNameCacheNextPruneUtc = DateTime.MinValue;
            }
            lock (personaLookupExceptionLock)
            {
                lastPersonaLookupExceptionUtc = DateTime.MinValue;
                suppressedPersonaLookupExceptions = 0;
            }
            PersonaNameLookup = sid => SteamFriends.GetFriendPersonaName(new CSteamID(sid));
            UtcNow = () => DateTime.UtcNow;
        }

        internal static void ResetPhotonRoomStateForTests()
        {
            PhotonInRoom = () => PhotonNetwork.InRoom;
            CurrentPhotonRoom = () => PhotonNetwork.CurrentRoom;
        }

        static void LogWarning(string message)
        {
            try
            {
                if (TryLogWarningWithNetLogger(message)) return;
            }
            catch (Exception ex)
            {
                LogWarningOrTrace($"[NetworkingPhotonExtensions] Failed to write warning log. Exception: {ex.GetType().Name}: {ex.Message}. Original message: {message}");
                return;
            }
            LogWarningOrTrace(message);
        }

        static bool TryLogWarningWithNetLogger(string message)
        {
            var logger = Net.Logger;
            if (logger == null) return false;
            logger.LogWarning(message);
            return true;
        }

        static void LogWarningOrTrace(string message)
        {
            try { Debug.LogWarning(message); }
            catch { System.Diagnostics.Trace.TraceWarning(message); }
        }

        static void LogPersonaLookupExceptionThrottled(Exception ex, ulong steamId)
        {
            var now = UtcNow();
            lock (personaLookupExceptionLock)
            {
                if (lastPersonaLookupExceptionUtc != DateTime.MinValue && now >= lastPersonaLookupExceptionUtc && now - lastPersonaLookupExceptionUtc < PersonaLookupExceptionCooldown)
                {
                    suppressedPersonaLookupExceptions++;
                    return;
                }

                var suppressed = suppressedPersonaLookupExceptions;
                suppressedPersonaLookupExceptions = 0;
                lastPersonaLookupExceptionUtc = now;
                var suffix = suppressed > 0 ? $" Suppressed {suppressed} similar exceptions." : string.Empty;
                LogWarning($"Steam persona lookup failed for lobby member {steamId}. Exception: {ex.GetType().Name}: {ex.Message}.{suffix}");
            }
        }

        static void LogUnresolvedMappingWarning(int actorNumber, string issueText, string message)
        {
            if (!ShouldEmitUnresolvedMappingWarning(actorNumber, issueText, UtcNow())) return;
            LogWarning(message);
        }

        internal static bool ShouldEmitUnresolvedMappingWarning(int actorNumber, string issueText, DateTime nowUtc)
        {
            var key = BuildUnresolvedMappingWarningKey(actorNumber, issueText);
            lock (UnresolvedMappingWarningThrottleLock)
            {
                if (nowUtc >= UnresolvedMappingWarningNextPruneUtc)
                {
                    PruneUnresolvedMappingWarningThrottle(nowUtc);
                    UnresolvedMappingWarningNextPruneUtc = nowUtc + UnresolvedMappingWarningCooldown;
                }
                if (UnresolvedMappingWarningThrottle.TryGetValue(key, out var previous)
                    && nowUtc >= previous
                    && nowUtc - previous < UnresolvedMappingWarningCooldown) return false;
                UnresolvedMappingWarningThrottle[key] = nowUtc;
                CapUnresolvedMappingWarningThrottle();
                return true;
            }
        }

        internal static void ResetUnresolvedMappingWarningThrottleForTests()
        {
            lock (UnresolvedMappingWarningThrottleLock)
            {
                UnresolvedMappingWarningThrottle.Clear();
                UnresolvedMappingWarningNextPruneUtc = DateTime.MinValue;
            }
        }

        internal static int GetUnresolvedMappingWarningThrottleCountForTests()
        {
            lock (UnresolvedMappingWarningThrottleLock) return UnresolvedMappingWarningThrottle.Count;
        }

        static void PruneUnresolvedMappingWarningThrottle(DateTime nowUtc)
        {
            if (UnresolvedMappingWarningThrottle.Count == 0) return;

            var expiredBeforeUtc = nowUtc - UnresolvedMappingWarningPruneAge;
            List<string>? keysToRemove = null;
            foreach (var entry in UnresolvedMappingWarningThrottle)
            {
                if (entry.Value >= expiredBeforeUtc) continue;
                if (keysToRemove == null) keysToRemove = new List<string>();
                keysToRemove.Add(entry.Key);
            }

            if (keysToRemove == null) return;
            for (int i = 0; i < keysToRemove.Count; i++) UnresolvedMappingWarningThrottle.Remove(keysToRemove[i]);
        }

        static void CapUnresolvedMappingWarningThrottle()
        {
            if (UnresolvedMappingWarningThrottle.Count <= UnresolvedMappingWarningThrottleMaxEntries) return;

            var oldestKey = string.Empty;
            var oldestTime = DateTime.MaxValue;
            foreach (var entry in UnresolvedMappingWarningThrottle)
            {
                if (entry.Value >= oldestTime) continue;
                oldestTime = entry.Value;
                oldestKey = entry.Key;
            }

            if (!string.IsNullOrEmpty(oldestKey)) UnresolvedMappingWarningThrottle.Remove(oldestKey);
        }

        static string BuildUnresolvedMappingWarningKey(int actorNumber, string issueText)
        {
            var normalizedIssue = string.IsNullOrWhiteSpace(issueText) ? "unknown_issue" : issueText.Trim().ToLowerInvariant();
            return actorNumber + "|" + normalizedIssue;
        }
    }
}
