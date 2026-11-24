output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "aks_name" {
  value = azurerm_kubernetes_cluster.main.name
}

output "acr_login_server" {
  value = azurerm_container_registry.main.login_server
}

output "key_vault_name" {
  value = azurerm_key_vault.main.name
}

output "redis_secret_name" {
  description = "Key Vault secret that holds ConnectionStrings__redis."
  value       = azurerm_key_vault_secret.redis.name
}

output "redis_connection_string" {
  description = "Also available directly: terraform output -raw redis_connection_string"
  value       = local.redis_connection_string
  sensitive   = true
}
