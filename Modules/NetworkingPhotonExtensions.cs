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
        internal enum StableIdentityResolutionStatus
        {
            Resolved,
            NoStableDataPresent,
            StableDataNoLobbyMatch,
            AmbiguousStableMatch
        }

        internal readonly struct StableIdentityResolution
        {
            public StableIdentityResolutionStatus Status { get; }
            public ulong MatchedSteamId { get; }
            public string Issue { get; }
            public bool IsResolved => Status == StableIdentityResolutionStatus.Resolved;

            public StableIdentityResolution(StableIdentityResolutionStatus status, ulong matchedSteamId, string issue)
            {
                Status = status;
                MatchedSteamId = matchedSteamId;
                Issue = issue;
            }
        }

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

                var stableResolution = TryResolveStableIdentity(player, lobbyIdSet);
                if (stableResolution.IsResolved)
                {
                    map[actor] = stableResolution.MatchedSteamId;
                    continue;
                }

                if (!ShouldAllowNicknameFallback(stableResolution.Status))
                {
                    var issue = stableResolution.Issue ?? "ambiguous stable identity";
                    LogWarning($"Photon actor {actor} has no Steam mapping ({issue}); actor left unmapped.");
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
                    var issue = stableResolution.Issue ?? "no stable identity present and no nickname match found";
                    LogWarning($"Photon actor {actor} has no Steam mapping ({issue}); actor left unmapped.");
                }
            }

            return map;
        }

        static StableIdentityResolution TryResolveStableIdentity(Player player, HashSet<ulong> lobbyIdSet)
        {
            return ResolveStableIdentityCandidates(GetStableIdCandidates(player), lobbyIdSet, player?.ActorNumber ?? -1);
        }

        internal static StableIdentityResolution ResolveStableIdentityCandidates(IEnumerable<ulong> stableIdCandidates, HashSet<ulong> lobbyIdSet, int actorNumber = -1)
        {
            if (stableIdCandidates == null || lobbyIdSet == null || lobbyIdSet.Count == 0)
                return new StableIdentityResolution(StableIdentityResolutionStatus.NoStableDataPresent, 0, "no stable ID data present");

            var allStableCandidates = new HashSet<ulong>();
            var lobbyMatchedCandidates = new HashSet<ulong>();
            foreach (var candidate in stableIdCandidates)
            {
                if (candidate == 0) continue;
                allStableCandidates.Add(candidate);
                if (lobbyIdSet.Contains(candidate)) lobbyMatchedCandidates.Add(candidate);
            }

            if (lobbyMatchedCandidates.Count == 1)
            {
                foreach (var candidate in lobbyMatchedCandidates)
                    return new StableIdentityResolution(StableIdentityResolutionStatus.Resolved, candidate, null);
            }

            if (lobbyMatchedCandidates.Count > 1)
            {
                var issue = $"multiple stable IDs matched lobby members ({string.Join(",", lobbyMatchedCandidates)})";
                if (actorNumber >= 0) LogWarning($"Photon actor {actorNumber} has ambiguous stable identity: {issue}.");
                return new StableIdentityResolution(StableIdentityResolutionStatus.AmbiguousStableMatch, 0, issue);
            }

            if (allStableCandidates.Count > 0)
                return new StableIdentityResolution(StableIdentityResolutionStatus.StableDataNoLobbyMatch, 0, "stable ID present but no candidate matched lobby members");

            return new StableIdentityResolution(StableIdentityResolutionStatus.NoStableDataPresent, 0, "no stable ID data present");
        }

        internal static bool ShouldAllowNicknameFallback(StableIdentityResolutionStatus status)
        {
            return status != StableIdentityResolutionStatus.AmbiguousStableMatch;
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
