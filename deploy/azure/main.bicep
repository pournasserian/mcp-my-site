// mcp-my-site on Azure App Service. See section 10.2 of docs/user-guide.md for the deployment steps.
//
// Names follow the Cloud Adoption Framework pattern <type>-<workload>-<environment>-<region>, with the
// recommended abbreviations (log, appi, st, cr, kv, asp, app). Resources whose names must be unique across
// Azure also get a 4-character suffix derived from the resource group, so two deployments of this
// template never collide. The storage account and key vault are capped at 24 characters, which is
// why workload is limited to 9 characters and the key vault name leaves out the region.

metadata description = 'mcp-my-site server on Azure App Service with its container registry, index storage, Key Vault and Application Insights.'

@description('Short workload name used in every resource name. Lowercase letters and digits only.')
@minLength(2)
@maxLength(9)
param workload string = 'mcpmysite'

@description('Environment name used in resource names and tags.')
@allowed([
  'dev'
  'test'
  'stage'
  'prod'
])
param environmentName string = 'prod'

@description('Azure region for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Short region code for resource names. Empty picks the code for the location from the table below.')
@maxLength(4)
param regionCode string = ''

@description('App Service plan SKU. B1 is enough for a site under 1,000 pages.')
param appServicePlanSku string = 'B1'

@description('Repository and tag of the server image in the container registry.')
param imageName string = 'mcp-my-site'
param imageTag string = 'latest'

@description('Key for the OpenAI-compatible embeddings endpoint, used to embed search queries. Stored in Key Vault.')
@secure()
param aiApiKey string

@description('Embeddings endpoint. Must match the one the index was built with.')
param embeddingsEndpoint string = 'https://openrouter.ai/api/v1'

@description('Optional key that MCP clients must send. Empty leaves the MCP endpoint open. Stored in Key Vault.')
@secure()
param mcpApiKey string = ''

@description('Optional origin of the site, such as https://example.com, allowed to call the search API from a browser.')
param siteOrigin string = ''

@description('Give the identity running the deployment write access to the index container, so it can upload the index with --auth-mode login.')
param grantDeployerIndexUpload bool = true

@description('Extra tags merged into the default workload and environment tags.')
param tags object = {}

// Common short codes for the regions most likely to be used; any other region falls back to its first
// four letters unless regionCode is set.
var regionCodes = {
  australiaeast: 'aue'
  brazilsouth: 'brs'
  canadacentral: 'cac'
  canadaeast: 'cae'
  centralindia: 'inc'
  centralus: 'cus'
  eastasia: 'ea'
  eastus: 'eus'
  eastus2: 'eus2'
  francecentral: 'frc'
  germanywestcentral: 'gwc'
  japaneast: 'jpe'
  northcentralus: 'ncus'
  northeurope: 'neu'
  southcentralus: 'scus'
  southeastasia: 'sea'
  swedencentral: 'sdc'
  switzerlandnorth: 'szn'
  uksouth: 'uks'
  ukwest: 'ukw'
  westeurope: 'weu'
  westus: 'wus'
  westus2: 'wus2'
  westus3: 'wus3'
}
var region = !empty(regionCode) ? regionCode : (regionCodes[?location] ?? take(location, 4))
var suffix = take(uniqueString(subscription().id, resourceGroup().id), 4)
var baseName = '${workload}-${environmentName}-${region}'

var names = {
  logAnalytics: 'log-${baseName}'
  appInsights: 'appi-${baseName}'
  storageAccount: 'st${workload}${environmentName}${region}${suffix}'
  containerRegistry: 'cr${workload}${environmentName}${region}${suffix}'
  keyVault: 'kv-${workload}-${environmentName}-${suffix}'
  appServicePlan: 'asp-${baseName}'
  webApp: 'app-${baseName}-${suffix}'
}

var allTags = union({ workload: workload, environment: environmentName }, tags)

// Built-in role definition IDs
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

// Monitoring

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logAnalytics
  location: location
  tags: allTags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.appInsights
  location: location
  tags: allTags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Index storage. The server downloads the index over plain HTTPS, so the container allows anonymous
// read of blobs (not listing). Everything in the index was public on the site already. Writes need
// Entra ID: shared keys are off.

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storageAccount
  location: location
  tags: allTags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: true
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource indexContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'index'
  properties: {
    publicAccess: 'Blob'
  }
}

resource deployerIndexUpload 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantDeployerIndexUpload) {
  name: guid(storage.id, deployer().objectId, roles.storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: deployer().objectId
  }
}

// Container registry. The web app pulls with its managed identity; the admin user stays off.

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.containerRegistry
  location: location
  tags: allTags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// Secrets

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.keyVault
  location: location
  tags: allTags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource aiApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ai-api-key'
  properties: {
    value: aiApiKey
  }
}

resource mcpApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(mcpApiKey)) {
  parent: keyVault
  name: 'mcp-api-key'
  properties: {
    value: mcpApiKey
  }
}

// App Service

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: names.appServicePlan
  location: location
  tags: allTags
  kind: 'linux'
  sku: {
    name: appServicePlanSku
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: names.webApp
  location: location
  tags: allTags
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/${imageName}:${imageTag}'
      acrUseManagedIdentityCreds: true
      alwaysOn: appServicePlanSku != 'F1'
      healthCheckPath: '/healthz'
      http20Enabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }
}

resource webAppAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, webApp.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webAppSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// The template owns the app settings: a redeploy replaces settings changed in the portal.
resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: webApp
  name: 'appsettings'
  properties: union(
    {
      WEBSITES_PORT: '8080'
      WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'false'
      Index__Url: '${storage.properties.primaryEndpoints.blob}${indexContainer.name}/'
      Ui__PublicUrl: 'https://${webApp.properties.defaultHostName}'
      Embeddings__Endpoint: embeddingsEndpoint
      AI_API_KEY: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${aiApiKeySecret.name})'
      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    },
    empty(mcpApiKey) ? {} : { Mcp__ApiKey: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=mcp-api-key)' },
    empty(siteOrigin) ? {} : { SearchApi__AllowedOrigins__0: siteOrigin }
  )
  dependsOn: [
    webAppSecretsUser
    mcpApiKeySecret
  ]
}

// Container output and HTTP logs, so a failed image pull or a startup error is visible in Log Analytics.
resource webAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: webApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServicePlatformLogs'
        enabled: true
      }
    ]
  }
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output mcpEndpoint string = 'https://${webApp.properties.defaultHostName}/mcp'
output containerRegistryName string = registry.name
output storageAccountName string = storage.name
output indexUrl string = '${storage.properties.primaryEndpoints.blob}${indexContainer.name}/'
output keyVaultName string = keyVault.name
