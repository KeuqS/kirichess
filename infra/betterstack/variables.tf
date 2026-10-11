variable "betteruptime_api_token" {
  description = "Better Stack Uptime API token (Team Settings -> API tokens, on betterstack.com/uptime). Manages monitors and escalation policies. Separate product/token from Telemetry."
  type        = string
  sensitive   = true
}

variable "telemetry_api_token" {
  description = "Better Stack Telemetry API token (Team Settings -> API tokens, on betterstack.com/logs). Manages the Prometheus scrape source, dashboard and metric alerts."
  type        = string
  sensitive   = true
}

variable "backend_base_url" {
  description = "Base URL of the deployed backend, no trailing slash. Both /health/ready and /metrics hang off this."
  type        = string
  default     = "https://kirichess-api.fly.dev"
}

variable "metrics_scrape_token" {
  description = "Must equal the Metrics__ScrapeToken Fly secret on the target app - MetricsEndpoint.cs 401s the scrape otherwise. Never defaulted here; pass it explicitly so a plan never silently scrapes with an empty bearer token."
  type        = string
  sensitive   = true
}

variable "environment" {
  description = "Distinguishes production from staging in resource names, since both fly.backend.toml and fly.backend.staging.toml expose the same endpoints on different hosts."
  type        = string
  default     = "production"
}

variable "alert_policy_id" {
  description = "Better Stack escalation policy ID to page instead of e-mailing the whole team. Leave null until an on-call policy exists (there is no human on-call rotation yet - see ROADMAP.md 8.7)."
  type        = number
  default     = null
}

variable "edge_api_url" {
  description = "Public API origin, through Cloudflare, no trailing slash. 10c.5's edge monitor hits /health/ready here. Distinct from backend_base_url, which is the Fly hostname the metrics scrape uses so a Cloudflare incident is visible as 'edge down, origin up'."
  type        = string
  default     = "https://api.kirichess.com"
}

variable "edge_site_url" {
  description = "Public site origin, through Cloudflare, no trailing slash. Expected status is 200 (I-5-5 / DH.3); a Cloudflare 5xx still pages."
  type        = string
  default     = "https://kirichess.com"
}
