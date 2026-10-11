terraform {
  required_version = ">= 1.5.0"

  required_providers {
    # Uptime checks, escalation policies, incident routing.
    betteruptime = {
      source  = "BetterStackHQ/better-uptime"
      version = "~> 0.21"
    }
    # Metrics ingestion (Prometheus scrape), dashboards, threshold alerts.
    logtail = {
      source  = "BetterStackHQ/logtail"
      version = "~> 10.17"
    }
  }
}

provider "betteruptime" {
  api_token = var.betteruptime_api_token
}

provider "logtail" {
  api_token = var.telemetry_api_token
}
