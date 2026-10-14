using 'main.bicep'

param workload = 'mcpmysite'
param environmentName = 'prod'

// Secrets come from the environment of the shell running the deployment, never from this file.
// AI_API_KEY is required; MCP_API_KEY is optional and leaves the MCP endpoint open when unset.
param aiApiKey = readEnvironmentVariable('AI_API_KEY')
param mcpApiKey = readEnvironmentVariable('MCP_API_KEY', '')

// Set to the site's origin, such as https://example.com, to let its search box call the search API.
param siteOrigin = ''
