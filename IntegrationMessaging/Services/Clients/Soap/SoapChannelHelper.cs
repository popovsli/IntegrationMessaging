// Services/Clients/Soap/SoapChannelHelper.cs
using System.ServiceModel;

internal static class SoapChannelHelper
{
    internal static void SafeClose(ICommunicationObject channel)
    {
        if (channel is null || channel.State == CommunicationState.Closed)
            return;

        try
        {
            if (channel.State != CommunicationState.Faulted)
                channel.Close();
            else
                channel.Abort();
        }
        catch
        {
            channel.Abort();
        }
    }
}