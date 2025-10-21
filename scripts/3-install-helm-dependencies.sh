cd ../deploy/helm

for chart in *; do
  if [ -f "$chart/Chart.yaml" ]; then
    echo "Installing dependencies for chart: $chart"
    helm dependency update "$chart"
  fi
done
