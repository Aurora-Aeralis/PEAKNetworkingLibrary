using Steamworks;

namespace NetworkingLibrary.Modules
{
    public readonly struct RPCInfo
    {
        public readonly ulong SteamId64;
        public readonly string SteamIdString;
        /// <summary>
        /// True when the sender was locally looped back instead of received from the network transport.
        /// </summary>
        public readonly bool IsLocalLoopback;

        public RPCInfo(ulong steamId64, string steamIdString, bool isLocalLoopback = false)
        {
            SteamId64 = steamId64;
            SteamIdString = steamIdString;
            IsLocalLoopback = isLocalLoopback;
        }

        public RPCInfo(ulong steamId64) : this(steamId64, steamId64.ToString(), false) { }
        public RPCInfo(CSteamID sid) : this(sid.m_SteamID, sid.ToString(), false) { }

    }
}
