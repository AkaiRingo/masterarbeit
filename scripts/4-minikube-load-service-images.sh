echo "Loading Docker images into Minikube..."

echo "🚀 [1/4] Loading order image"
minikube image load order:latest --overwrite=true

echo "🚀 [2/4] Loading payment image"
minikube image load payment:latest --overwrite=true

echo "🚀 [3/4] Loading inventory image"
minikube image load inventory:latest --overwrite=true

echo "🚀 [4/4] Loading fulfillment image"
minikube image load fulfillment:latest --overwrite=true

echo "✅ All images loaded into Minikube"
