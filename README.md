# Quest 3 WiFi-Heatmap

Native Meta-Quest-3-App (Unity 6, **OpenXR + Meta-OpenXR/AR Foundation** — nicht
das alte Meta XR SDK), die die WiFi-Signalstärke (RSSI) des **verbundenen**
Access Points ausliest und mit der 3D-Kopfposition verknüpft. Beim
Durchschreiten der Räume entstehen im **MR-Passthrough**:

- eine **3D-Punktwolke** (Kugeln, RdYlGn: grün = stark … rot = schwach),
- eine **interpolierte Boden-Heatmap** (IDW) mit **Lücken-Anzeige** — noch
  nicht vermessene Bereiche erscheinen grau gerastert,
- optional das **Raum-Mesh** des Tiefenscans (Space Setup) als Geist-Overlay —
  so lässt sich ein Gebäude scannen und das WiFi-Signal gemeinsam mit der
  Geometrie analysieren.

> **Warum nativ und nicht WebXR?** Browser/WebXR haben keine API für die
> WiFi-Signalstärke (die Network Information API liefert nur den Verbindungstyp,
> nie dBm). RSSI ist nur über native Android-APIs (`WifiManager`) erreichbar.

## Datenausgabe
- **Live-Heatmap** im Headset (Punktwolke + Bodenfläche, umschaltbar)
- **CSV/JSON-Export** auf das Gerät (per `adb pull` abrufbar)
- **PNG-Export** der Boden-Heatmap (`<stem>_heatmap.png`, volle Deckkraft,
  grau = unvermessen) + `<stem>_heatmap.json` mit Georeferenz (Welt-Ursprung,
  Zellgröße, Bodenhöhe) → am PC pixelgenau unter CSV/OBJ legbar
- **OBJ-Export** des gescannten Raum-Meshes — gleicher Dateistamm und **gleiches
  Koordinatensystem** wie die CSV-Samples → am PC deckungsgleich ladbar
- **Live-Stream** per UDP an einen PC (`tools/receiver.py`)

---

## Projektstruktur
```
Wifi-Scanner/Assets/
  Scripts/
    Sample.cs                     Datenmodell eines Messpunkts (+ CSV/JSON)
    WifiReader.cs                 Android-WifiManager-Bridge (JNI, RSSI/SSID/…)
    SampleCollector.cs            Orchestrator: Abtastung Position+RSSI, Glättung,
                                  Spatial-Hash-Grid, Statistik, Export-Fassade
    HeatmapColorMapper.cs         RSSI (dBm) -> Farbe (RdYlGn, wie receiver.py)
    HeatmapLegend.cs              Gradient-Textur für die Farblegenden
    PointCloudRenderer.cs         Punktwolke (gepoolte Kugeln, 48 Farbstufen,
                                  Deckkraft + RSSI-Bereichsfilter)
    FloorHeatmapRenderer.cs       Boden-Heatmap: IDW-Interpolation auf Textur,
                                  Lücken-Anzeige, Deckkraft + Filter
    VisualizationModeController.cs Ansicht: Punktwolke / Heatmap / Beides
    RoomMeshController.cs         3D-Raumrekonstruktion (Scene Mesh) + OBJ-Export
    ControlPanel.cs               Interaktives World-Space-Panel (XRI-Ray)
    HudController.cs              Handgelenk-HUD (linker Controller) + Legende
    RecordingControls.cs          Controller-Tasten (A/B/X)
    OpacityControls.cs            L-Stick regelt Deckkraft
    PassthroughSetup.cs           MR-Passthrough + Boundaryless zur Laufzeit
    SceneBootstrap.cs             Erzeugt/verdrahtet ALLES zur Laufzeit
    DataExporter.cs               CSV/JSON/OBJ-Export nach persistentDataPath
    NetworkStreamer.cs            UDP-Live-Stream an PC
    WifiProbe.cs                  Standalone-Gerätetest (Schritt 0)
  PointMat.mat / FloorHeatMat.mat / RoomMeshMat.mat
                                  URP-Unlit-Material-ASSETS (siehe Magenta-Falle)
  Plugins/Android/AndroidManifest.xml  Permissions + benannte GameActivity
tools/
  receiver.py                     PC-seitiger UDP-Empfänger -> wifi_live.csv
```

---

## Setup

### Voraussetzungen
- **Unity 6000.0.x** mit Android Build Support (+ OpenJDK, SDK, NDK)
- Pakete (bereits im Manifest): `com.unity.xr.openxr`, `com.unity.xr.meta-openxr`,
  `com.unity.xr.arfoundation`, `com.unity.xr.interaction.toolkit`, `com.unity.xr.hands`
- Quest 3 / 3s im **Developer-Mode**, USB-Debugging erlaubt

### Einmalige Project Settings (nicht skriptbar)
Project Settings → XR Plug-in Management → **OpenXR → Android**:
Feature-Gruppe **„Meta Quest“** aktivieren und diese Features anhaken:
1. **Meta Quest: Session** (Pflicht-Abhängigkeit)
2. **Meta Quest: Camera (Passthrough)**
3. **Meta Quest: Boundary Visibility** (Boundaryless-Modus)
4. **Meta Quest: Meshing** (Raum-Mesh; injiziert die `USE_SCENE`-Permission
   automatisch ins Manifest) — *in diesem Repo bereits aktiviert*

### Szene
`Assets/Scenes/SampleScene.unity` ist fertig eingerichtet: XR Origin (XRI-
Hands-Rig), `SceneBootstrap` (mit zugewiesenen Material-Assets), `PassthroughSetup`.
Nur die **PC-IP** für den UDP-Stream am `SceneBootstrap` anpassen.

### ⚠️ URP-Magenta-Falle
Zur Laufzeit per `Shader.Find` erzeugte Materialien verlieren im Android-Build
ihre Shader-Variante und rendern magenta. Deshalb sind **PointMat**,
**FloorHeatMat** und **RoomMeshMat** echte Material-Assets (URP/Unlit) und per
Inspector an `SceneBootstrap` gehängt — so bleiben die Varianten im Build.

---

## Bedienung

Die App startet direkt mit laufender Aufnahme (Passthrough, Boundary aus).

**Controller:**
| Eingabe | Funktion |
|---|---|
| A (rechts) | Aufnahme Pause/Weiter |
| B (rechts) | Export (CSV + JSON + OBJ) |
| X (links) | Reset — Punktwolke leeren |
| L-Stick ↑/↓ | Deckkraft (Punkte + Heatmap) |
| R-Stick-Klick | Ansicht wechseln: Punktwolke → Heatmap → Beides |
| Menü-Taste (links) | Bedienpanel ein-/ausblenden (erscheint vor dir) |

**Bedienpanel** (mit dem Controller-Strahl anvisieren, Trigger = Klick):
Aufnahme/Export/Löschen, Ansichtsmodus, **„Raum scannen“** (öffnet das
System-Space-Setup), Raum-Mesh-Modus **aus → Geist → verdeckt** (verdeckt =
Occlusion: reale Wände/Möbel aus dem Raum-Scan verbergen Punkte und Heatmap
dahinter — man sieht nur die Daten des eigenen Raums; technisch ein
Alpha-0-Material mit ZWrite vor der Transparent-Queue, das Passthrough-Pendant
zum „Selective Passthrough“ des Meta-SDK), Deckkraft-Slider, **RSSI-Bereichsfilter**
(z. B. nur Schwachstellen < −75 dBm zeigen), Farblegende und Live-Statistik
(Min/Max/Ø-RSSI, Punktzahl, Aufnahmedauer).

**Handgelenk-HUD** (linkes Handgelenk): REC-Status, Punkte, RSSI, SSID,
Ansicht, Deckkraft, Tastenbelegung, Farblegende.

### Gebäude + WiFi gemeinsam analysieren
1. Pro Raum einmal **„Raum scannen“** (Space Setup) ausführen — das Scene Mesh
   ist der tiefensensor-basierte Raum-Scan des Systems, **kein** Live-SLAM.
2. Räume ablaufen, bis die Boden-Heatmap keine grauen Lücken mehr zeigt.
3. **Export**: erzeugt `wifi_<zeitstempel>.csv/.json/.obj` sowie
   `…_heatmap.png/.json` mit identischem Koordinatensystem
   (Unity-Tracking-Space, linkshändig — das OBJ ist bewusst NICHT in die
   OBJ-Konvention gespiegelt, damit es zu posX/Y/Z der CSV passt).
4. Am PC z. B. in Blender/Python OBJ + CSV zusammen laden.

### Daten vom Gerät holen
```bash
adb pull /sdcard/Android/data/<package-name>/files/   ./
```

---

## Bekannte Risiken / Hinweise
- **WiFi-Permission auf Quest**: `ACCESS_FINE_LOCATION` muss zur Laufzeit
  gewährt sein, sonst SSID/BSSID redaktiert (`<unknown ssid>` / `rssi -127`).
  Am 27.06.2026 auf Gerät bestätigt: funktioniert mit gewährter Permission.
- **`getConnectionInfo()`** ist ab API 31 deprecated, funktioniert aber. Falls
  ein OS-Update das blockt: in `WifiReader.cs` auf `ConnectivityManager` +
  `NetworkCallback` + `getTransportInfo()` umstellen.
- **GameActivity-Einstiegspunkt**: `UnityPlayer.currentActivity` ist `null` —
  `WifiReader` holt den Context über `ActivityThread.currentApplication()`.
- **Tracking-Drift** über mehrere Räume möglich → Wolke/Mesh können sich
  verziehen. Während einer Aufnahme **nicht** recentern.
- **RSSI rauscht** ±mehrere dB → `smoothingWindow` glättet; pro 0,2-m-Zelle
  wird zusätzlich der laufende Mittelwert gebildet.
- **Bodenhöhe der Heatmap**: Auf dem Gerät liefert der Floor-Tracking-Origin
  der Quest die echte Bodenhöhe (y = 0 des XR Origin, vom System kalibriert);
  nur im Editor wird aus dem tiefsten Messpunkt − 1,2 m geschätzt. Bei mehreren
  Etagen bleibt es EINE Fläche auf Höhe des kalibrierten Bodens — obere Etagen
  bräuchten eine Fläche pro Etage (z. B. via Scene-Mesh-/Plane-Erkennung).
- **Passthrough/Scene Mesh** funktionieren nur im Android-Build. Im Editor
  liefert `WifiReader` distanzabhängige Fake-Werte, damit Punktwolke, Heatmap,
  Modi, Panel und Filter ohne Gerät testbar sind.
