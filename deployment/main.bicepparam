using './main.bicep'

param baseName = 'docing'

// Mandatory params
param userPrincipalId = ''

// Naming params
param managedIdentity_name = '${baseName}useridentity'
param storage_name = '${baseName}blobacc'
param function_app_name = '${baseName}funcapp'
param document_intelligence_name = '${baseName}docintl'
param open_ai_name = '${baseName}openai'

// Common params
param tags = {}

// Storage params
param storage_containers = [
  {
    name: 'documents'
  }
]

//azure sql params
param azuresqldb_name =  '${baseName}db'
param azuresqlServerName =  '${baseName}server'
// Function app params
param function_app_storageSkuName = 'Standard_LRS'

// Document Intelligence Params
param document_intelligence_sku = {
  name: 'S0'
}
param document_intelligence_publicNetworkAccess = 'Enabled'
param document_intelligence_disableLocalAuth = false

// Open AI params
param modelDeployment = 'text-embedding-3-large'
param modelDimensions = '1536'
param open_ai_deployments = [
  {
    name: modelDeployment
    sku: {
      name: 'Standard'
      capacity: 10
    }
    model: {
      name: modelDeployment
      version: '1'
    }
  }
]
param open_ai_sku = 'S0'
param open_ai_kind = 'OpenAI'
param open_ai_format = 'OpenAI'
param open_ai_publicNetworkAccess = 'Enabled'
