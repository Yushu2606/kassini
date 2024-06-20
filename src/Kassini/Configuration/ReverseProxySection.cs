using Yarp.ReverseProxy.Configuration;

namespace Kassini.Configuration;

public class ReverseProxySection
{
    public RouteConfig[] Routes { get; set; } = [];
    
    public ClusterConfig[] Clusters { get; set; } = [];
}
