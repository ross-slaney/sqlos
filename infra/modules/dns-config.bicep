targetScope = 'resourceGroup'

@description('Azure DNS zone name.')
param dnsZoneName string

@description('Static IPv4 address for the apex A record.')
param apexIPv4Address string

@description('Container App FQDN used by the www CNAME record.')
param containerAppFqdn string

@description('Container App custom domain verification ID.')
param customDomainVerificationId string

@description('TTL for DNS records.')
param ttl int = 300

resource dnsZone 'Microsoft.Network/dnsZones@2023-07-01-preview' existing = {
  name: dnsZoneName
}

resource apexRecord 'Microsoft.Network/dnsZones/A@2023-07-01-preview' = {
  parent: dnsZone
  name: '@'
  properties: {
    TTL: ttl
    ARecords: [
      {
        ipv4Address: apexIPv4Address
      }
    ]
  }
}

resource apexVerificationRecord 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  parent: dnsZone
  name: 'asuid'
  properties: {
    TTL: ttl
    TXTRecords: [
      {
        value: [
          customDomainVerificationId
        ]
      }
    ]
  }
}

resource wwwRecord 'Microsoft.Network/dnsZones/CNAME@2023-07-01-preview' = {
  parent: dnsZone
  name: 'www'
  properties: {
    TTL: ttl
    CNAMERecord: {
      cname: containerAppFqdn
    }
  }
}

resource wwwVerificationRecord 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  parent: dnsZone
  name: 'asuid.www'
  properties: {
    TTL: ttl
    TXTRecords: [
      {
        value: [
          customDomainVerificationId
        ]
      }
    ]
  }
}

output apexRecordCreated bool = !empty(apexRecord.id)
output wwwRecordCreated bool = !empty(wwwRecord.id)
