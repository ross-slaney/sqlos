targetScope = 'resourceGroup'

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Project prefix for naming.')
param projectPrefix string

@description('Container Apps environment ID.')
param containerAppsEnvId string

@description('Container Apps environment static public IP.')
param containerAppsEnvStaticIp string

@description('ACR login server.')
param acrLoginServer string

@description('Container image reference.')
param containerImage string

@description('User-assigned managed identity resource ID.')
param uamiId string

@description('Resource group that contains the Azure DNS zone.')
param dnsResourceGroup string

@description('Azure DNS zone name.')
param dnsZoneName string

var containerAppName = '${projectPrefix}-prod-web'

module containerAppModule '../modules/container-app-uami.bicep' = {
  name: 'containerApp'
  params: {
    containerAppName: containerAppName
    location: location
    managedEnvironmentId: containerAppsEnvId
    acrLoginServer: acrLoginServer
    uamiId: uamiId
    containerImage: containerImage
  }
}

module dnsModule '../modules/dns-config.bicep' = {
  name: 'dnsConfig'
  scope: resourceGroup(subscription().subscriptionId, dnsResourceGroup)
  params: {
    dnsZoneName: dnsZoneName
    apexIPv4Address: containerAppsEnvStaticIp
    containerAppFqdn: containerAppModule.outputs.containerAppFqdn
    customDomainVerificationId: containerAppModule.outputs.customDomainVerificationId
  }
}

output containerAppFqdn string = containerAppModule.outputs.containerAppFqdn
output containerAppName string = containerAppModule.outputs.containerAppName
output customDomainVerificationId string = containerAppModule.outputs.customDomainVerificationId
