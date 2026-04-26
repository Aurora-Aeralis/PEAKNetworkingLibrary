using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using NetworkingLibrary.Services;

namespace NetworkingLibrary.Modules
{
    public static class NetworkingPhotonExtensions
    {
        static readonly TimeSpan UnresolvedMappingWarningCooldown = TimeSpan.FromSeconds(7);
        static readonly TimeSpan UnresolvedMappingWarningPruneAge = TimeSpan.FromSeconds(UnresolvedMappingWarningCooldown.TotalSeconds * 4);
        const int UnresolvedMappingWarningThrottleMaxEntries = 2048;
        static readonly Dictionary<string, DateTime> UnresolvedMappingWarningThrottle = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        static readonly object UnresolvedMappingWarningThrottleLock = new object();
        static readonly string[] StableIdPropertyKeys =
        {
            "steam64", "steamid64", "steam_id64", "steamid", "steam_id", "steam", "authid", "auth_id", "userid", "user_id"
        };

        public static Dictionary<int, ulong> MapPhotonActorsToSteam(INetworkingService svc)
        {
            var map = new Dictionary<int, ulong>();
            if (svc == null) return map;

            var lobbyIds = svc.GetLobbyMemberSteamIds();
            if (lobbyIds == null || lobbyIds.Length == 0) return map;
            if (!PhotonNetwork.InRoom) return map;
            var room = PhotonNetwork.CurrentRoom;
            if (room == null) return map;

            var lobbyIdSet = new HashSet<ulong>(lobbyIds);
            var personaByName = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
            foreach (var sid in lobbyIds)
            {
                try
                {
                    var name = SteamFriends.GetFriendPersonaName(new CSteamID(sid));
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!personaByName.TryGetValue(name, out var ids)) personaByName[name] = ids = new List<ulong>(1);
                    ids.Add(sid);
                }
                catch { }
            }

            foreach (var kv in room.Players)
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

        static bool TryResolveStableIdentity(Player player, HashSet<ulong> lobbyIdSet, out ulong matchedSteamId, out string issue)
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

        static IEnumerable<ulong> GetStableIdCandidates(Player player)
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
                case ulong u when u > 0:
                    steamId = u;
                    return true;
                case long l when l > 0:
                    steamId = (ulong)l;
                    return true;
                case uint ui when ui > 0:
                    steamId = ui;
                    return true;
                case int i when i > 0:
                    steamId = (ulong)i;
                    return true;
                case string s when !string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out var parsed) && parsed > 0:
                    steamId = parsed;
                    return true;
                default:
                    return false;
            }
        }

        static void LogWarning(string message)
        {
            try { Net.Logger?.LogWarning(message); } catch { }
        }

        static void LogUnresolvedMappingWarning(int actorNumber, string issueText, string message)
        {
            if (!ShouldEmitUnresolvedMappingWarning(actorNumber, issueText, DateTime.UtcNow)) return;
            LogWarning(message);
        }

        internal static bool ShouldEmitUnresolvedMappingWarning(int actorNumber, string issueText, DateTime nowUtc)
        {
            var key = BuildUnresolvedMappingWarningKey(actorNumber, issueText);
            lock (UnresolvedMappingWarningThrottleLock)
            {
                PruneUnresolvedMappingWarningThrottle(nowUtc);
                if (UnresolvedMappingWarningThrottle.TryGetValue(key, out var previous) && nowUtc - previous < UnresolvedMappingWarningCooldown) return false;
                UnresolvedMappingWarningThrottle[key] = nowUtc;
                CapUnresolvedMappingWarningThrottle();
                return true;
            }
        }

        internal static void ResetUnresolvedMappingWarningThrottleForTests()
        {
            lock (UnresolvedMappingWarningThrottleLock) UnresolvedMappingWarningThrottle.Clear();
        }

        internal static int GetUnresolvedMappingWarningThrottleCountForTests()
        {
            lock (UnresolvedMappingWarningThrottleLock) return UnresolvedMappingWarningThrottle.Count;
        }

        static void PruneUnresolvedMappingWarningThrottle(DateTime nowUtc)
        {
            if (UnresolvedMappingWarningThrottle.Count == 0) return;

            var expiredBeforeUtc = nowUtc - UnresolvedMappingWarningPruneAge;
            List<string> keysToRemove = null;
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
