cd ../deploy/helm

echo "Upgrade observability"
helm upgrade observability ./observability -nobservability
echo "Upgrade target"
helm upgrade target ./shop -n target
echo "Upgrade chaos"
helm upgrade chaos-mesh ./chaos-mesh -n chaos
