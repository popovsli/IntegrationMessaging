// Systems/BdzSiteSchedule/BdzSiteScheduleHandler.cs
using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Handlers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class BdzSiteScheduleHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.BdzSiteSchedule;

    private static readonly XmlSerializerNamespaces EmptyNamespaces =
        new(new[] { new XmlQualifiedName("", "") });

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        if (q.MessageOperation != MessageOperation.Create &&
            q.MessageOperation != MessageOperation.Update)
            throw new IntegrationMessagingException(
                $"{MessageTypeName} only supports Create/Update operations.");

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        // Deserialize the queue payload back to the DTO
        var dto = JsonSerializer.Deserialize<BdzTimeTableDto>(q.Payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IntegrationMessagingException(
                $"{MessageTypeName} payload could not be deserialized to BdzTimeTableDto.");

        // Serialize to XML with Windows-1251 (exactly as original)
        var xmlPayload = SerializeToXml(dto);

        return new IntegrationRequest
        {
            EndpointPath = resolution.ResolvedPath,
            HttpMethod = resolution.HttpMethod,
            Payload = xmlPayload,
            Metadata = new Dictionary<string, string>
            {
                ["FileName"] = dto.FileName ?? "upload.xml"
            }
        };
    }

    private static string SerializeToXml(BdzTimeTableDto dto)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.GetEncoding(1251),
            Indent = false,
            OmitXmlDeclaration = false
        };

        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, settings);
        var serializer = new XmlSerializer(typeof(BdzTimeTableDto));
        serializer.Serialize(writer, dto, EmptyNamespaces);
        return sb.ToString();
    }
}

public class BdzTimeTableDto
{
    public string? FileName { get; set; }
    public string? Content { get; set; }
}