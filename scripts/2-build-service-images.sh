cd ../services

echo "Building Docker images for all services..."

echo "🔨 [1/4] Building order image"
docker build -t order:latest -f order/Dockerfile .

echo "🔨 [2/4] Building payment image"
docker build -t payment:latest -f payment/Dockerfile .

echo "🔨 [3/4] Building inventory image"
docker build -t inventory:latest -f inventory/Dockerfile .

echo "🔨 [4/4] Building fulfillment image"
docker build -t fulfillment:latest -f fulfillment/Dockerfile .

echo "✅ Created all images"