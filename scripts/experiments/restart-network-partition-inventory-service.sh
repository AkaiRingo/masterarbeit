cd ../../deploy/helm/chaos-mesh/chaos-experiments/workflows

kubectl delete -f networkpartition-inventory-service.yaml -n chaos

kubectl apply -f networkpartition-inventory-service.yaml -n chaos