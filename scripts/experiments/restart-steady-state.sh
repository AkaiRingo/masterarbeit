cd ../../deploy/helm/chaos-mesh/chaos-experiments/workflows

kubectl delete -f steady-state.yaml -n chaos

kubectl apply -f steady-state.yaml -n chaos