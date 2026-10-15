using 'main.bicep'

param workload = 'mcpmysite'
param environmentName = 'prod'

// Secrets come from the environment of the shell running the deployment, never from this file.
// AI_API_KEY is required; MCP_API_KEY is optional and leaves the MCP endpoint open when unset.
param aiApiKey = readEnvironmentVariable('AI_API_KEY')
param mcpApiKey = readEnvironmentVariable('MCP_API_KEY', '')

// Set to the site's origin, such as https://example.com, to let its search box call the search API.
param siteOrigin = ''

// Where alerts (5xx, container restarts, no running replica) are sent. Empty creates the rules without notifying anyone.
param alertEmail = ''

// The first deployment sets this to false: the app cannot start until its image is in the registry.
// Run az acr build, then deploy again with the default.
param deployApp = true

// Custom domain behind Cloudflare, for example ai.example.com. Leave empty until the Origin CA certificate
// has been imported into Key Vault (user guide, section 10.2), then set it and deploy again.
param customDomain = ''
