-- ============================================================
-- IntegrationMessaging — DB seed data
-- Run after EF migrations have created the schema
-- ============================================================

-- REST/JWT system
INSERT INTO IntegrationSystem VALUES (
    'PORT_A', 'api_user', 'secret123', 30,
    'Port Authority A REST', 1,
    'https://api.port-a.example.com', 'api/messages',
    'auth/token', 60, 10, 'REST_JWT', 'JSON',
    5, 15, 10, 30, 'JSON', '{}', SYSDATETIMEOFFSET()
);

-- SOAP/WCF system
INSERT INTO IntegrationSystem VALUES (
    'WASTE_SOAP', 'svc_user', 'p@ssw0rd', 30,
    'Waste Management WCF Service', 1,
    'https://waste.example.com/services', 'WasteService.svc',
    '', 120, 5, 'SOAP_BASIC_HTTPS', 'XML',
    3, 30, 5, 60, 'XML', '{}', SYSDATETIMEOFFSET()
);

-- Endpoints for REST system
INSERT INTO IntegrationEndpoint (IntegrationSystemCode, MessageTypeName, EndpointPath, HttpMethod, SoapAction, Description) VALUES
    ('PORT_A', 'WasteNotification',   'api/waste/notifications',            'POST', NULL, 'Waste notification endpoint'),
    ('PORT_A', 'WasteRequest',        'api/waste/requests',                 'POST', NULL, 'Waste request endpoint'),
    ('PORT_A', 'SSNNotification',     'api/ssn/{EntityId}/notify',          'PUT',  NULL, 'SSN vessel notification'),
    ('PORT_A', 'SHIPSANNotification', 'api/shipsan/messages',               'POST', NULL, 'SHIPSAN notification'),
    ('PORT_A', 'PCSNotification',     'api/pcs/messages',                   'POST', NULL, 'PCS port clearance');

-- Endpoints for SOAP system
INSERT INTO IntegrationEndpoint (IntegrationSystemCode, MessageTypeName, EndpointPath, HttpMethod, SoapAction, Description) VALUES
    ('WASTE_SOAP', 'WasteNotification',
     'WasteService.svc', 'POST',
     'http://tempuri.org/IWasteService/SubmitWasteNotification',
     'WCF Waste Notification'),

    ('WASTE_SOAP', 'WasteRequest',
     'WasteRequestService.svc', 'POST',
     'http://tempuri.org/IWasteRequestService/SubmitWasteRequest',
     'WCF Waste Request');

-- Sample queue messages
INSERT INTO IntegrationMessageQueue
    (EntityId, IntegrationSystemCode, MessageOperation, Payload, Status, CreationTime, MessageTypeName)
VALUES
    (101, 'PORT_A', 'Create',
     '{"VesselCallId":101,"WasteTypes":["Oily"]}',
     'Queued', GETUTCDATE(), 'WasteNotification'),

    (101, 'PORT_A', 'Update',
     '{"VesselCallId":101,"WasteTypes":["Oily","Sewage"]}',
     'Queued', GETUTCDATE(), 'WasteNotification');
