using System.ServiceModel;
using System.ServiceModel.Channels;
using IntegrationMessaging.Entities;

namespace IntegrationMessaging.Services.Clients.Soap;

public interface ISoapChannelFactory
{
    ChannelFactory<IRequestChannel> GetOrCreate(IntegrationSystem system);
    void Invalidate(string integrationSystemCode);
}
