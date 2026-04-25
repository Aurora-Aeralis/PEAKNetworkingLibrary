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
                        LogWarning($"Photon actor {actor} nickname '{player.NickName}' is ambiguous across {nicknameMatches.Count} lobby members; actor left unmapped.");
                    }
                }
                else
                {
                    var issue = stableIssue ?? "no stable identity present and no nickname match found";
                    LogWarning($"Photon actor {actor} has no Steam mapping ({issue}); actor left unmapped.");
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
                issue = $"multiple stable IDs matched lobby members ({string.Join(",", candidates)})";
                LogWarning($"Photon actor {player.ActorNumber} has ambiguous stable identity: {issue}.");
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
    }
}
