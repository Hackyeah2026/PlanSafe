namespace PlanSafe.App.Services;

public static class EvacuationApiUrlResolver
{
    public static string Resolve(string clientBaseUrl)
    {
        var clientUri = new Uri(clientBaseUrl);
        var endpoint = clientUri.Port switch
        {
            5050 => (Scheme: "http", Port: 5051),
            5171 => (Scheme: "http", Port: 49492),
            7030 => (Scheme: "https", Port: 49491),
            _ => (Scheme: clientUri.Scheme, Port: -1)
        };

        // Hosted deployments route /api through the same origin and base path.
        if (endpoint.Port == -1) return clientBaseUrl.TrimEnd('/');

        return new UriBuilder(endpoint.Scheme, clientUri.Host, endpoint.Port)
            .Uri.AbsoluteUri.TrimEnd('/');
    }
}
