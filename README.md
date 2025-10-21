# Proaktives Risikomanagement durch Chaos Engineering: Ein Framework für resiliente Softwarelösungen in Kubernetes

## Abstract

Die Etablierung von Kubernetes als Standard für die Container-Orchestrierung hat neue Ebenen der Komplexität und neuartige Fehlermodi in verteilten Systemen hervorgebracht. Traditionelle Testmethoden erweisen sich häufig als unzureichend, um die Resilienz dieser dynamischen, cloudnativen Architekturen unter realistischen Fehlerbedingungen zu überprüfen. Die vorliegende Arbeit greift diese Herausforderung auf, indem sie ein Framework für proaktives Risikomanagement in Kubernetes konzipiert und implementiert, das auf den Prinzipien des Chaos Engineering basiert. Ziel ist es, eine systematische und empirische Methodik zur Bewertung und Verbesserung der Resilienz von Softwarelösungen bereitzustellen.

Das Framework integriert eine microservicebasierte Referenzanwendung, einen Observability-Stack, der Prometheus und Grafana zur Metrikerfassung und Visualisierung nutzt, sowie Chaos-Mesh zur kontrollierten Injektion von Fehlern. Ein hypothesengesteuerter Ansatz wurde verwendet, um eine Reihe kontrollierter Experimente durchzuführen, darunter Pod-Ausfälle, Netzwerkpartitionen und den Ausfall einer zustandsbehafteten Datenbankkomponente. Das Systemverhalten wurde anhand vordefinierter Service Level Objectives unter Verwendung von Schlüsselmetriken, insbesondere der Four Golden Signals, bewertet.

Die Evaluation demonstrierte die Effektivität des Frameworks bei der Aufdeckung kritischer Schwachstellen. Die Experimente zeigten, dass die Resilienz des Systems durch suboptimale Autoscaling-Konfigurationen, das Fehlen von Fehlertoleranzmustern zur Handhabung synchroner Dienstabhängigkeiten bei Netzwerkausfällen und eine kritische Fehlkonfiguration, die zu Datenverlust in der Persistenzschicht führte, beeinträchtigt wurde. In allen Szenarien wurden signifikante Verletzung der Service Level Objectives beobachtet, was belegt, dass das System realistischen Störungen nicht standhalten konnte.

Letztlich validiert diese Arbeit Chaos Engineering als ein wirksames Instrument des proaktiven Risikomanagements. Das entwickelte Framework bietet einen praktischen und reproduzierbaren Ansatz, um verborgene Schwächen empirisch zu identifizieren, das Systemverhalten unter turbulenten Bedingungen zu validieren und handlungsrelevante Erkenntnisse für die Entwicklung resilienterer, produktionsreifer Anwendungen in Kubernetes-Umgebungen abzuleiten.

# Übersicht
Im folgenden sind verschiedene Diagramme dargestellt, die die Architektur und Komponenten des Systems veranschaulichen.

## 📦 Shop - Sequenzdiagramm des Referenzsystems

```mermaid
sequenceDiagram
    actor User
    participant OrderService
    participant InventoryService
    participant PaymentService
    participant MessageBroker
    participant FulfillmentService

    User->>OrderService: POST /orders
    OrderService->>InventoryService: POST /inventory/reserve
    InventoryService-->>OrderService: Erfolg / Fehler

    OrderService->>PaymentService: POST /payments
    PaymentService-->>OrderService: Erfolg / Fehler

    OrderService->>OrderService: Bestellung speichern
    OrderService->>MessageBroker: Publish(orderId)

    MessageBroker->>FulfillmentService: orderId

    FulfillmentService->>OrderService: PUT /orders/{id}/status (Completed)
```

## Chaos-Mesh Architektur

```mermaid
graph TD
    subgraph ChaosMesh
        Controller[Chaos Controller Manager]
        Daemon[Chaos Daemon]
    end

    subgraph CRDs
        WorkflowCRD[Workflow CRD]
        SchedulerCRD[Scheduler CRD]
        NetworkChaos[Network Chaos CRD]
    end

    Controller -->|Schedules & Manages Experiments| Daemon
    Controller --> WorkflowCRD
    Controller --> SchedulerCRD
    Controller --> NetworkChaos
    Daemon -->|Executes Chaos in Target Pods| NetworkChaos
```

## Metric-Flow

```mermaid
---
config:
  layout: elk
---
flowchart RL
    subgraph "Visualisierung"
        Grafana
    end

    subgraph "Datenhaltung"
        Prometheus(Prometheus<br><i>Metriken</i>)
        Loki(Loki<br><i>Logs</i>)
    end

    subgraph "Applikation"
        Service[Service]
    end

    %% Metrik-Fluss (Pull-basiert)
    Grafana -- "Abfrage via<br>PromQL" --> Prometheus
    Prometheus -- "Scraping via<br>/metrics (Pull)" --> Service

    %% Log-Fluss (Push-basiert)
    Grafana -- "Abfrage via<br>LogQL" --> Loki
    Service -- "Senden via<br>Serilog (Push)" --> Loki
```

## Gesamtarchitektur
```mermaid
---
config:
  layout: elk
---
flowchart TB
  %% Target Namespace
  subgraph Target["Namespace: target"]
    direction LR
    Order["Order-Service"]
    Inventory["Inventory-Service"]
    Payment["Payment-Service"]
    Fulfillment["Fulfillment-Service"]
    RabbitMQ["RabbitMQ"]
  end

  %% Observability Namespace
  subgraph Observability["Namespace: observability"]
    direction LR
    Grafana["Grafana"]
    Prometheus["Prometheus"]
    Loki["Loki"]
  end

  %% Chaos Namespace
  subgraph Chaos["Namespace: chaos"]
    direction LR
    ChaosMesh["Chaos Mesh"]
  end

  %% Connections
  Target -->|metrics| Prometheus
  Target -->|logs| Loki

  Grafana -->|reads metrics| Prometheus
  Grafana -->|reads logs| Loki

  ChaosMesh -->|inject experiments| Target
  ChaosMesh -->|metrics| Prometheus
```

## RabbitMQ 

```mermaid
graph LR
%% Exchanges
    EX[Fanout-Exchange orders.exchange]

%% Queues
    Q2[Fulfillment Queue]

%% Services
    OS[Order Service]
    FS[Fulfillment Service]

%% Order Service publishes messages
    OS -->|publish orderId| EX

%% Exchange routes messages to queues
    EX -->|route| Q2

%% Fulfillment Service consumes from its queue
    Q2 -->|consume orderId| FS

```
