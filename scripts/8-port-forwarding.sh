#!/bin/bash

set -e

port_forward() {
  local namespace=$1
  local service=$2
  local local_port=$3
  local remote_port=$4
  local name=$5
  local use_swagger_path=$6

  if kubectl get svc "$service" -n "$namespace" &>/dev/null; then
    kubectl port-forward -n "$namespace" svc/"$service" "$local_port":"$remote_port" >/dev/null 2>&1 &
    local url="http://localhost:$local_port"
    if [ "$use_swagger_path" == "true" ]; then
      url="$url/swagger"
    fi
    echo "  ✅ $name läuft auf $url"
  else
    echo "  ⚠️  $name nicht gefunden (Namespace: $namespace, Service: $service)"
  fi
}

echo "🚀 Starte Port-Forwarding..."

# --- target ---
if kubectl get ns target &>/dev/null; then
  echo ""
  echo "➡️  target-Services:"
  port_forward target order-service         8081 80  "Order Service"         true
  port_forward target inventory-service     8082 80  "Inventory Service"     true
  port_forward target payment-service       8083 80  "Payment Service"       true
  port_forward target rabbitmq              5672 5672 "RabbitMQ AMQP"         false
  port_forward target rabbitmq             15672 15672 "RabbitMQ UI"           false
fi

# --- Observability ---
if kubectl get ns observability &>/dev/null; then
  echo ""
  echo "➡️  Telemetry:"
  port_forward observability observability-grafana           3000 80 "Grafana"          false
  port_forward observability observability-prometheus-server  9090 80 "Prometheus"       false
fi

echo ""
echo "✅ Alle verfügbaren Port-Forwardings wurden gestartet."
