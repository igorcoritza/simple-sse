variable "subscription_id" {
  description = "Azure subscription to deploy into."
  type        = string
}

variable "name" {
  description = "Base name for every resource (lowercase letters and digits)."
  type        = string
  default     = "ssedemo"
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "westeurope"
}

variable "aks_node_size" {
  description = "VM size of the AKS system node pool."
  type        = string
  default     = "Standard_B2s"
}
