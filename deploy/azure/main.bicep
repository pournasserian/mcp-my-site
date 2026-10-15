// mcp-my-site on Azure Container Apps behind Cloudflare. See section 10.2 of docs/user-guide.md for the
// deployment steps.
//
// Names follow the Cloud Adoption Framework pattern <type>-<workload>-<environment>-<region>, with the
// recommended abbreviations (log, appi, st, cr, kv, id, cae, ca, ag). Resources whose names must be unique across
// Azure also get a 4-character suffix derived from the resource group, so two deployments of this
// template never collide. The storage account and key vault are capped at 24 characters, which is
// why workload is limited to 9 characters and the key vault name leaves out the region.

metadata description = 'mcp-my-site server on Azure Container Apps behind Cloudflare, with its container registry, index storage, Key Vault, Application Insights and alerts.'

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

@description('Repository and tag of the server image in the container registry.')
param imageName string = 'mcp-my-site'
param imageTag string = 'latest'

@description('Create the container app. Set false on the very first deployment: the app cannot start before its image is in the registry, so deploy once without it, run az acr build, then deploy again.')
param deployApp bool = true

@description('Most replicas the HTTP concurrency rule may scale out to. The app always keeps one.')
@minValue(1)
@maxValue(10)
param maxReplicas int = 2

@description('Concurrent HTTP requests per replica that trigger a scale-out.')
@minValue(1)
param httpConcurrency int = 30

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

@description('Log request and response bodies of the MCP endpoint and search API to Application Insights (Telemetry:LogBodies). Includes search text, so it is off by default.')
param logBodies bool = false

@description('Allow ingress only from the Cloudflare IPv4 ranges, so the generated azurecontainerapps.io host name cannot be used to bypass Cloudflare. Turn off for a first test without Cloudflare.')
param lockIngressToCloudflare bool = true

@description('Cloudflare IPv4 ranges, from https://www.cloudflare.com/ips-v4 (last checked 2026-10-15). Container Apps ingress rules accept IPv4 only.')
param cloudflareIpv4Ranges array = [
  '173.245.48.0/20'
  '103.21.244.0/22'
  '103.22.200.0/22'
  '103.31.4.0/22'
  '141.101.64.0/18'
  '108.162.192.0/18'
  '190.93.240.0/20'
  '188.114.96.0/20'
  '197.234.240.0/22'
  '198.41.128.0/17'
  '162.158.0.0/15'
  '104.16.0.0/13'
  '104.24.0.0/14'
  '172.64.0.0/13'
  '131.0.72.0/22'
]

@description('Custom domain, such as ai.example.com. Leave empty until the Cloudflare Origin CA certificate is in Key Vault; set it on a second deployment to bind the domain.')
param customDomain string = ''

@description('Name of the Origin CA certificate in Key Vault, imported with az keyvault certificate import before customDomain is set.')
param originCertificateName string = 'origin-ca'

@description('Email address for alerts. Empty creates the alert rules without notifying anyone.')
param alertEmail string = ''

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
  identity: 'id-${baseName}'
  containerEnvironment: 'cae-${baseName}'
  containerApp: 'ca-${baseName}-${suffix}'
  actionGroup: 'ag-${baseName}'
  alertPrefix: 'alert-${baseName}'
}

var allTags = union({ workload: workload, environment: environmentName }, tags)

// Built-in role definition IDs
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

var appPort = 8080

// Allow rules only: with any allow rule present, everything else is denied.
var cloudflareRestrictions = [for (range, i) in cloudflareIpv4Ranges: {
  name: 'cloudflare-${i}'
  description: 'Cloudflare edge'
  ipAddressRange: range
  action: 'Allow'
}]

// Where visitors reach the app: the custom domain once it is bound, otherwise the generated host name.
var generatedHost = '${names.containerApp}.${environment.properties.defaultDomain}'
var publicHost = empty(customDomain) ? generatedHost : customDomain

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

// Container registry. The container app pulls with its user-assigned identity; the admin user stays off.

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

// Identity. One user-assigned identity serves the container app (image pull, Key Vault secret references) and
// the Container Apps environment (importing the Origin CA certificate from Key Vault). Its roles are assigned
// before either is created, which a system-assigned identity cannot do: that one only exists once the app does.

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: allTags
}

resource identityAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource identitySecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Container Apps environment on the Consumption workload profile. Console and system logs go to the same
// Log Analytics workspace as Application Insights.

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: names.containerEnvironment
  location: location
  tags: allTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  dependsOn: [
    identitySecretsUser
  ]
}

// The Cloudflare Origin CA certificate, imported into Key Vault with az keyvault certificate import before
// customDomain is set. Container Apps keeps the environment copy in step when the vault certificate is renewed.
resource originCertificate 'Microsoft.App/managedEnvironments/certificates@2025-01-01' = if (!empty(customDomain)) {
  parent: environment
  name: 'cert-${replace(customDomain, '.', '-')}'
  location: location
  tags: allTags
  properties: {
    certificateKeyVaultProperties: {
      identity: identity.id
      keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${originCertificateName}'
    }
  }
}

// The app. Ingress is open to the internet by design, but allows only Cloudflare's addresses.

resource containerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: names.containerApp
  location: location
  tags: allTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: appPort
        transport: 'auto'
        allowInsecure: false
        ipSecurityRestrictions: lockIngressToCloudflare ? cloudflareRestrictions : []
        customDomains: empty(customDomain) ? [] : [
          {
            name: customDomain
            certificateId: originCertificate!.id
            bindingType: 'SniEnabled'
          }
        ]
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      // Key Vault references. The values never appear in the template or the app's configuration.
      secrets: union(
        [
          {
            name: 'ai-api-key'
            keyVaultUrl: aiApiKeySecret.properties.secretUri
            identity: identity.id
          }
        ],
        empty(mcpApiKey) ? [] : [
          {
            name: 'mcp-api-key'
            keyVaultUrl: mcpApiKeySecret!.properties.secretUri
            identity: identity.id
          }
        ]
      )
    }
    template: {
      containers: [
        {
          name: 'server'
          image: '${registry.properties.loginServer}/${imageName}:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: union(
            [
              { name: 'Index__Url', value: '${storage.properties.primaryEndpoints.blob}${indexContainer.name}/' }
              { name: 'Ui__PublicUrl', value: 'https://${publicHost}' }
              { name: 'Embeddings__Endpoint', value: embeddingsEndpoint }
              { name: 'AI_API_KEY', secretRef: 'ai-api-key' }
              { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            ],
            empty(mcpApiKey) ? [] : [{ name: 'Mcp__ApiKey', secretRef: 'mcp-api-key' }],
            empty(siteOrigin) ? [] : [{ name: 'SearchApi__AllowedOrigins__0', value: siteOrigin }],
            logBodies ? [{ name: 'Telemetry__LogBodies', value: 'true' }] : []
          )
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/healthz', port: appPort }
              initialDelaySeconds: 2
              periodSeconds: 3
              timeoutSeconds: 3
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: appPort }
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/healthz', port: appPort }
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 3
              successThreshold: 1
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: string(httpConcurrency)
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    identityAcrPull
    identitySecretsUser
  ]
}

// Alerts

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: names.actionGroup
  location: 'global'
  tags: allTags
  properties: {
    groupShortName: take(workload, 12)
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

var alertActions = empty(alertEmail) ? [] : [{ actionGroupId: actionGroup!.id }]

resource alert5xx 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployApp) {
  name: '${names.alertPrefix}-5xx'
  location: 'global'
  tags: allTags
  properties: {
    description: 'The server answered more than 5 requests with a 5xx status in 5 minutes.'
    severity: 2
    enabled: true
    scopes: [containerApp!.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'server-errors'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'microsoft.app/containerapps'
          metricName: 'Requests'
          dimensions: [
            {
              name: 'statusCodeCategory'
              operator: 'Include'
              values: ['5xx']
            }
          ]
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Total'
        }
      ]
    }
    actions: alertActions
  }
}

resource alertRestarts 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployApp) {
  name: '${names.alertPrefix}-restarts'
  location: 'global'
  tags: allTags
  properties: {
    description: 'A container restarted. Failing liveness probes, crashes and out-of-memory kills all count.'
    severity: 2
    enabled: true
    scopes: [containerApp!.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'restarts'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'microsoft.app/containerapps'
          metricName: 'RestartCount'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
        }
      ]
    }
    actions: alertActions
  }
}

resource alertNoReplicas 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployApp) {
  name: '${names.alertPrefix}-no-replicas'
  location: 'global'
  tags: allTags
  properties: {
    description: 'No replica is running. The app keeps at least one, so zero means it is down.'
    severity: 1
    enabled: true
    scopes: [containerApp!.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'replicas'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'microsoft.app/containerapps'
          metricName: 'Replicas'
          operator: 'LessThan'
          threshold: 1
          timeAggregation: 'Minimum'
        }
      ]
    }
    actions: alertActions
  }
}

output containerAppName string = deployApp ? containerApp!.name : ''
output containerAppFqdn string = deployApp ? containerApp!.properties.configuration.ingress.fqdn : ''
output environmentDefaultDomain string = environment.properties.defaultDomain
output customDomainVerificationId string = environment.properties.customDomainConfiguration.customDomainVerificationId
output publicUrl string = 'https://${publicHost}'
output mcpEndpoint string = 'https://${publicHost}/mcp'
output containerRegistryName string = registry.name
output storageAccountName string = storage.name
output indexUrl string = '${storage.properties.primaryEndpoints.blob}${indexContainer.name}/'
output keyVaultName string = keyVault.name
