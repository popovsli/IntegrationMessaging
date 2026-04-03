using IntegrationMessaging.Services.IntegrationClients.DTOs;
using System.ServiceModel;

namespace IntegrationMessaging.Services.IntegrationClients.Contracts
{
    public interface IWasteService
    {
        Task CancelWasteNotificationAsync(CancelWasteNotificationRequest cancelWasteNotificationRequest);

        [OperationContract(Name = "ZWS_SUVR_POST_ORDERS", Action = "urn:sap-com:document:sap:rfc:functions:ZWS_SUVR_POST_ORDERS:ZWS_SUVR_POST_ORDERSRequest", ReplyAction = "*")]
        [XmlSerializerFormat(SupportFaults = true)]
        Task PostOrdersAsync(WasteNotificationDto request);
        Task<SubmitWasteNotificationResponse> SubmitWasteNotificationAsync(
             SubmitWasteNotificationRequest request);
    }

    public class SubmitWasteNotificationRequest
    {
        public int VesselCallId { get; set; }
        public string WasteTypeCode { get; set; }
        public double QuantityM3 { get; set; }
        public string PortCode { get; set; }
    }

    public class CancelWasteNotificationRequest
    {
        public int VesselCallId { get; set; }
        public DateTime CancelledAtUtc { get; set; }
    }

    public class SubmitWasteNotificationResponse
    {
        public string ConfirmationRef { get; set; }  // "WN-2026-00471"
        public DateTime AcceptedAtUtc { get; set; }
    }
}
