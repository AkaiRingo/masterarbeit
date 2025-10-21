#!/bin/bash

set -e

cd ../deploy/helm/shop

helm install target . --namespace=target --create-namespace