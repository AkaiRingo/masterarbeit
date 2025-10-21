cd ../deploy/helm/observability/dashboards

kubectl create configmap chaos-dashboard \
  --from-file=chaos-dashboard.json \
  -n observability \
  --dry-run=client -o yaml | kubectl replace -f -

kubectl label configmap chaos-dashboard grafana_dashboard="1" -n observability --overwrite
kubectl label configmap chaos-dashboard app.kubernetes.io/managed-by="Helm" -n observability --overwrite

kubectl annotate configmap chaos-dashboard \
  meta.helm.sh/release-name="observability" \
  meta.helm.sh/release-namespace="observability" \
  -n observability --overwrite
