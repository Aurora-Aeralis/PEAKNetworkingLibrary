namespace NetworkingLibrary.Services
{
    internal interface INetworkingServiceStateTransfer
    {
        void CopyRuntimeStateTo(INetworkingService target);
    }
}
