# FeatureLink Suite - Detailed Flow

Detailed version of the "How It All Fits Together" diagram. The root
[README.md](../README.md) carries a trimmed-down variant; this is the full one,
kept here for reuse in slides, docs, or other repos.

```mermaid
flowchart LR
    ESRI["<b>Esri ArcGIS</b><br/>hosted feature layer<br/>or saved web map"]

    BUILT["<b>Built Configuration</b><br/>TAK Portal Display Configurator<br/><i>symbology, labels, popups</i>"]
    DIRECT["<b>Direct Configuration</b><br/>ArcGIS sign-in, or paste a<br/>web map / feature layer URL"]
    DELIVER["<b>Delivery</b><br/>QR scan - TAK Portal link<br/>- paste URL"]

    TAK["<b>FeatureLink Plugin</b><br/>ATAK - WinTAK - CloudTAK"]

    FIELD["<b>On the Map</b><br/>styled layers + auto-generated icons"]
    PLI["<b>PLI</b><br/>shared position layer,<br/>auto-send + history"]
    SHARE["<b>Share</b><br/>CoT to the team,<br/>config to another EUD"]
    WB["<b>Edits + PLI Write Back</b><br/>map items and position history<br/>pushed to the hosted layer"]

    ESRI --> BUILT
    ESRI --> DIRECT
    BUILT --> DELIVER
    DIRECT --> DELIVER
    DELIVER --> TAK
    TAK --> FIELD
    TAK --> PLI
    FIELD --> SHARE
    TAK -.-> WB
    WB -.-> ESRI

    classDef esri fill:#DBEAFE,stroke:#2563EB,color:#0B1220;
    classDef cfg fill:#EDE9FE,stroke:#7C3AED,color:#0B1220;
    classDef tak fill:#FEF3C7,stroke:#D97706,color:#0B1220;
    classDef field fill:#DCFCE7,stroke:#16A34A,color:#0B1220;
    classDef wb fill:#FFE4E6,stroke:#E11D48,color:#0B1220;

    class ESRI esri;
    class BUILT,DIRECT,DELIVER cfg;
    class TAK tak;
    class FIELD,PLI,SHARE field;
    class WB wb;
```

*Esri holds the data - a config says how it should look - the plugin puts it on
the EUD - edits and PLI flow back to the same layer.*
