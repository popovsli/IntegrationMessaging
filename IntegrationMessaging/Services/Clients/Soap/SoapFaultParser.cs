using System.Xml;

namespace IntegrationMessaging.Services.Clients.Soap;

public sealed record SoapFault(string Code, string Reason, string? Detail = null);

public static class SoapFaultParser
{
    public static SoapFault? TryParse(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(responseXml);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("s11", "http://schemas.xmlsoap.org/soap/envelope/");
            nsMgr.AddNamespace("s12", "http://www.w3.org/2003/05/soap-envelope");

            var fault11 = doc.SelectSingleNode("//s11:Fault", nsMgr);
            if (fault11 is not null)
            {
                var code   = fault11.SelectSingleNode("faultcode")?.InnerText   ?? "Unknown";
                var reason = fault11.SelectSingleNode("faultstring")?.InnerText ?? "Unknown fault";
                var detail = fault11.SelectSingleNode("detail")?.InnerXml;
                return new SoapFault(code, reason, detail);
            }

            var fault12 = doc.SelectSingleNode("//s12:Fault", nsMgr);
            if (fault12 is not null)
            {
                var code   = fault12.SelectSingleNode("s12:Code/s12:Value", nsMgr)?.InnerText   ?? "Unknown";
                var reason = fault12.SelectSingleNode("s12:Reason/s12:Text", nsMgr)?.InnerText ?? "Unknown fault";
                var detail = fault12.SelectSingleNode("s12:Detail", nsMgr)?.InnerXml;
                return new SoapFault(code, reason, detail);
            }

            return null;
        }
        catch { return null; }
    }

    public static string? ExtractBody(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml)) return null;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(responseXml);
            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("s11", "http://schemas.xmlsoap.org/soap/envelope/");
            nsMgr.AddNamespace("s12", "http://www.w3.org/2003/05/soap-envelope");
            return doc.SelectSingleNode("//s11:Body", nsMgr)?.InnerXml
                ?? doc.SelectSingleNode("//s12:Body", nsMgr)?.InnerXml;
        }
        catch { return null; }
    }
}
