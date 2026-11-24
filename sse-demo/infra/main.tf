data "azurerm_client_config" "current" {}

# Short random suffix so globally unique names (ACR, Key Vault, Redis) don't collide.
resource "random_string" "suffix" {
  length  = 5
  special = false
  upper   = false
}

locals {
  suffix = random_string.suffix.result
  tags = {
    project = "sse-demo"
  }
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${var.name}"
  location = var.location
  tags     = local.tags
}

# ---------------------------------------------------------------- Container registry
resource "azurerm_container_registry" "main" {
  name                = "acr${var.name}${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = local.tags
}

# ---------------------------------------------------------------- Kubernetes (AKS)
resource "azurerm_kubernetes_cluster" "main" {
  name                = "aks-${var.name}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  dns_prefix          = var.name
  tags                = local.tags

  default_node_pool {
    name       = "system"
    vm_size    = var.aks_node_size
    node_count = 1
  }

  identity {
    type = "SystemAssigned"
  }

  # Fixed node pool (no automatic node provisioning).
  node_provisioning_profile {
    mode = "Manual"
  }

  # Application routing add-on: a managed nginx ingress controller
  # (ingress class "webapprouting.kubernetes.azure.com", used by the Helm chart).
  web_app_routing {
    dns_zone_ids = []
  }

  # Lets pods mount Key Vault secrets through the Secrets Store CSI driver (optional path, see README).
  key_vault_secrets_provider {
    secret_rotation_enabled = true
  }
}

# The cluster's kubelet identity can pull images from ACR.
resource "azurerm_role_assignment" "aks_acr_pull" {
  scope                            = azurerm_container_registry.main.id
  role_definition_name             = "AcrPull"
  principal_id                     = azurerm_kubernetes_cluster.main.kubelet_identity[0].object_id
  skip_service_principal_aad_check = true
}

# ---------------------------------------------------------------- Azure Managed Redis
resource "azurerm_managed_redis" "main" {
  name                = "redis-${var.name}-${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku_name            = "Balanced_B0"
  tags                = local.tags

  # Demo settings: single node, reachable over the internet with TLS + access key.
  # For production: high availability on and a private endpoint.
  high_availability_enabled = false
  public_network_access     = "Enabled"

  default_database {
    # Access keys keep the demo simple (a plain connection string).
    # For production, prefer Entra ID authentication with workload identity.
    access_keys_authentication_enabled = true
    # EnterpriseCluster exposes a single endpoint, so Hangfire and pub/sub work as with a single node.
    clustering_policy = "EnterpriseCluster"
  }
}

locals {
  # StackExchange.Redis connection string. Azure Managed Redis only accepts TLS connections.
  redis_connection_string = join(",", [
    "${azurerm_managed_redis.main.hostname}:${azurerm_managed_redis.main.default_database[0].port}",
    "password=${azurerm_managed_redis.main.default_database[0].primary_access_key}",
    "ssl=True",
    "abortConnect=False",
  ])
}

# ---------------------------------------------------------------- Key Vault
resource "azurerm_key_vault" "main" {
  name                       = "kv-${var.name}-${local.suffix}"
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  tags                       = local.tags
}

# Whoever runs Terraform (you, or the pipeline's service connection) can write and read secrets.
resource "azurerm_role_assignment" "kv_secrets_officer" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# The AKS Key Vault CSI identity can read secrets (for the CSI driver option).
resource "azurerm_role_assignment" "aks_kv_reader" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_kubernetes_cluster.main.key_vault_secrets_provider[0].secret_identity[0].object_id
}

# The Redis connection string, ready for the pipeline (or the CSI driver) to pick up.
resource "azurerm_key_vault_secret" "redis" {
  name         = "redis-connection-string"
  value        = local.redis_connection_string
  key_vault_id = azurerm_key_vault.main.id

  depends_on = [azurerm_role_assignment.kv_secrets_officer]
}
