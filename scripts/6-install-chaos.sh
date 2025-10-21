#!/bin/bash

set -e

cd ../deploy/helm/chaos-mesh

helm install chaos . --namespace=chaos --create-namespace