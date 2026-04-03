// Services/Clients/Soap/SoapFaultParser.cs
// FIX #8: XmlDocument DOM replaced with forward-only XmlReader to reduce
//          allocations on every SOAP response in high-throughput scenarios.

using IntegrationMessaging.Models;
using System.Xml;

namespace IntegrationMessaging.Services.Clients.Soap;

public sealed record SoapFault(string Code, string Reason, string? Detail = null);

public static class SoapFaultParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        IgnoreWhitespace = true,
        IgnoreComments = true,
        DtdProcessing = DtdProcessing.Prohibit,
        ConformanceLevel = ConformanceLevel.Document
    };

    // FIX #8: forward-only streaming parse — no XmlDocument allocation
    public static SoapFault? TryParse(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml)) return null;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(responseXml), ReaderSettings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || reader.LocalName != "Fault")
                    continue;

                // Found <soap:Fault> — read children
                string? code = null, reason = null, detail = null;

                while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement
                                          && reader.LocalName == "Fault"))
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;

                    switch (reader.LocalName)
                    {
                        // SOAP 1.1
                        case "faultcode":
                            code = reader.ReadElementContentAsString();
                            break;
                        case "faultstring":
                            reason = reader.ReadElementContentAsString();
                            break;
                        case "detail":
                            detail = reader.ReadInnerXml();
                            break;
                        // SOAP 1.2
                        case "Code":
                        case "Value":
                            code ??= reader.ReadElementContentAsString();
                            break;
                        case "Reason":
                        case "Text":
                            reason ??= reader.ReadElementContentAsString();
                            break;
                        case "Detail":
                            detail ??= reader.ReadInnerXml();
                            break;
                    }
                }

                return new SoapFault(
                    Code: code ?? "Unknown",
                    Reason: reason ?? "Unknown",
                    Detail: detail);
            }
        }
        catch (XmlException)
        {
            // Malformed response — not a SOAP fault we can parse
            return null;
        }

        return null;
    }

    /// <summary>
    /// Extracts the inner XML of &lt;soap:Body&gt; from a full SOAP envelope string.
    /// Returns null if the Body element is not found.
    /// </summary>
    public static string? ExtractBody(string envelopeXml)
    {
        if (string.IsNullOrWhiteSpace(envelopeXml)) return null;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(envelopeXml), ReaderSettings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Body")
                    return reader.ReadInnerXml();
            }
        }
        catch (XmlException)
        {
            return null;
        }

        return null;
    }
}
