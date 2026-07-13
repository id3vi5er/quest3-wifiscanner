# Quest 3 WiFi-Scanner – Neue Feature-Ideen und Implementierungsplan

Dieser Plan dokumentiert die geplanten Erweiterungen und neuen Features für das Projekt,
basierend auf den jüngsten Absprachen.

**Status (2026-07-10):** Alle Punkte sind implementiert — 1A (Audio-Feedback),
2A (Sessions laden + Ausrichten, inkl. Tool-Sidecars), 3A (Türen/Fenster),
3B (Multi-Etagen, inkl. Heatmap-PNG pro Etage) und 4 (Web-Dashboard).
Ausstehend ist nur noch der Gerätetest der jüngsten Ergänzungen.

---

## 1. Core: Erfassung & Feedback

### A) Audio Feedback ("Geigerzähler-Modus") — ERLEDIGT (2026-07-09)

Implementiert in `GeigerCounter.cs`: prozedural erzeugter Sinus-Blip (60 ms,
1320 Hz, Attack/Decay-Hüllkurve, kein Asset), Beep-Rate 0,5–12/s + Pitch 0,9–1,5
linear über −95…−40 dBm (Inspector-tunebar), Quelle `SampleCollector.LastRssi`
(läuft auch bei pausierter Aufnahme), Stille solange kein Signal. Wiring:
`SceneBootstrap.enableGeigerAudio`, Start-Zustand AUS; Panel-Button "Audio:
an/aus" in "Visualisierung". Gerätetest ausstehend.

---

## 2. Sessions, Notes & Tools

### A) Sessions Speichern und Laden (In-App) — ERLEDIGT (2026-07-10)

Implementiert als Laden + Ausrichten (die Quest setzt ihren Tracking-Ursprung bei
jedem Start neu — geladene Welt-Koordinaten liegen fast nie richtig):

* **Format:** `DataExporter.SampleList` enthält jetzt `floors` (FloorLevel-Liste)
  und `gridYawDegrees`; alte Dateien bleiben ladbar (Fallback: eine Etage "EG").
* **Laden:** `SessionLoader.cs` orchestriert — `DataExporter.TryLoadSamples/
  TryLoadRooms/ListScanStems`, `FloorManager.LoadFloors`,
  `SampleCollector.LoadSamples` (Bulk, ein `DataRevision++`),
  `RoomTracer.LoadRooms` (inkl. Öffnungen/Höhen), `SetGridYaw`; danach
  Re-Stream an receiver.py/Web-Dashboard (`{"type":"clear"}` + Samples in
  250er-Chunks; receiver.py hat jetzt einen clear-Handler).
* **Panel:** Reihe "Scan: <stem>" (Cycle) + "Laden" (Zwei-Klick-Bestätigung,
  ersetzt die Session, pausiert die Aufnahme) in "Aufnahme & Export".
* **Ausrichten:** `ScanAligner.cs` ("Scan ausrichten", IExclusiveTool):
  Wand des geladenen Grundrisses per Ray wählen (grüne Kugel = Anker-Ecke),
  dann linken Controller an die reale Ecke, rechten entlang der Wand, beide
  Grips halten → Starrkörper-Transformation (Yaw + XZ + Boden-Offset) auf
  Samples, Räume (inkl. Öffnungs-Höhen), Etagen und Grid-Yaw.
* **Follow-up (offen):** auch `_drill`/`_notes`/`_ping`/`_measure` mitladen —
  gleiche Mechanik, braucht je Tool eine kleine Load-API.

---

## 3. Architektur & Räume

### A) Türen und Fenster — ERLEDIGT (M17)

Implementiert in `OpeningTool.cs` + `RoomTracer.WallOpening`: Tür = obere Ecke +
Breite (Höhe auto bis Boden), Fenster = freies Rechteck; A = Typwechsel,
B = letzte Öffnung entfernen. OBJ-Export schneidet echte Löcher ins Wand-Mesh,
SVG zeichnet Wand-Lücken mit Viertelkreis (Tür) bzw. Doppellinie (Fenster).
Öffnungen streamen im rooms-Paket (receiver.py + Web-Dashboard zeichnen sie).

### B) Multi-Stockwerk Unterstützung — ERLEDIGT (M17), 1 Rest-Punkt

Implementiert in `FloorManager.cs`: Etagenliste (EG/OG1…), `Sample.floor`-Tagging
bei der Aufnahme, Auto-Switch der aktiven Etage beim Treppensteigen
(edge-triggered), Vorschlag "Etage anlegen" ab 2 m über der obersten Etage
(Panel-Button statt Handgelenk-Prompt), Boden je Etage über "Boden kalibrieren"
korrigierbar. Ansicht: Panel-Cycle EG → OG1 → … → Alle (statt Dropdown);
Punktwolken-Filter + Heatmap nur für die aktive Etage. Web-Dashboard hat ein
Etagen-Dropdown. 3D-Export ist automatisch gestapelt (Räume exportieren mit
ihrem echten `floorY`).

* **OFFEN (Rest):** separate Heatmap-PNGs pro Stockwerk. `ExportPng` schreibt
  heute nur die Heatmap der AKTIVEN Etage. Plan (optional, mittlerer Aufwand):
  `FloorHeatmapRenderer` beim Export je Etage mit Samples einmal offline
  durchrechnen (bestehende Compute/Colorize-Pässe mit Etagen-Filter) und als
  `<stem>_heatmap_EG.png`, `_OG1.png` … schreiben; Georeferenz-JSON je Datei.

---

## 4. PC Tools & Web-Dashboard

### Live Web-Dashboard direkt auf der Quest — ERLEDIGT (M18/M19)

Implementiert in `WebDashboardServer.cs` + `WebDashboardPage.cs`: eingebetteter
Server auf Port 8080 (TcpListener statt HttpListener — HttpListener-WebSockets
sind auf IL2CPP/Android unzuverlässig), eigener WebSocket-Handshake, serviert
eine self-contained HTML/JS-Seite. Live-Stream = dieselben JSON-Pakete wie an
receiver.py (Pose, Räume inkl. Öffnungen, Drill, AP, Ping, Notizen, Samples),
Snapshot-Replay für Nachzügler. Zusätzlich (über den Plan hinaus): Scan-Downloads
(`/scans`, `/download`, `/download-all` als ZIP, `/export`), clientseitige
IDW-Heatmap-Ansicht, Grid-Ausrichtung, Laufpfad-Layer, Etagen-Dropdown.
Panel: An/Aus-Button + URL-Anzeige.

---

## Empfohlene Reihenfolge

1. **3B-Rest PNG pro Etage** — ERLEDIGT (2026-07-10).
2. **2A-Follow-up** — ERLEDIGT (2026-07-10): `_drill`/`_notes`/`_ping`/`_measure`
   werden beim Scan-Laden wiederhergestellt und vom ScanAligner mittransformiert.

---

# Ideen-Backlog v2 (2026-07-10)

Erweiterungen, die auf dem JETZT vorhandenen Toolset aufbauen (Tab-Menü,
Tool-Palette, Räume mit Öffnungen, Multi-Etagen, Notizen inkl. „Virtueller AP“,
Ping-Knoten, Session-Laden, Web-Dashboard). Sortiert nach Kategorie; ★ = größter
Nutzen im Verhältnis zum Aufwand.

**Auswahl (2026-07-10):** **6, 7, 8, 9, 10, 11, 12, 16, 18, 20 — ALLE UMGESETZT**
(M24; Gerätetest ausstehend):
- 6 → `ApLocator` Pfadverlust-Fit (Pattern-Search + Log-Distance-Regression,
  Unsicherheitsring, Fit-Parameter in Paket/Export)
- 7 → `WallLossEstimator.cs` (distanzbereinigte Seitendifferenz je Wand,
  Labels, `_walls.json`, Panel-Toggle im Räume-Tab)
- 8 → `CoverageSimulator.cs` (Prognose-Quad aus virtuellen AP-Notizen bzw.
  realem Fit, Wanddurchgänge × Dämpfung, Panel-Toggle im Ansicht-Tab)
- 9 → Diff-Modus im `FloorHeatmapRenderer` (Referenz-Scan auf gleichem Raster,
  rot↔grau↔grün ±20 dB, Panel-Reihe „Vergleich/Diff“ im Aufnahme-Tab)
- 10 → `Haptics.cs` + Pulse in allen Tools/Palette
- 11 → `UndoStack.cs` (Snapshots + Load-API-Replays, ↺ auf der Palette)
- 12 → `ResumePrompt.cs` („Letzte Session fortsetzen?“ beim Start)
- 16 → `OnboardingGuide.cs` (6 Schritte, Auto-Weiter, „Tutorial starten“ im System-Tab)
- 18 → `ReportGenerator.cs` (`{stem}_report.html`, self-contained, am Export-Ende)
- 20 → 3D-Ansicht im Web-Dashboard (Orbit per Drag/Pinch, Etagen-Stapel)

**Update (2026-07-10, 2. Runde):** 2 (Roaming-Marker) und 3 (Band-Tagging) sind
UMGESETZT (M26) — erweitert um BSSID-getrennte Heatmaps: jede BSSID bekommt ihre
eigene Heatmap (Samples tragen die BSSID, das Punkt-Folding mittelt nicht mehr
über AP-Wechsel hinweg), Panel-Cycle „AP: Alle → einzelne BSSIDs“ (Alle =
Parallel-Ansicht).
**GESTRICHEN auf Nutzerwunsch:** 14 (Sprachdiktat — uninteressant),
17 (Minimap pinnen — bräuchte zweiten VR-Client + Sync),
19 (Screenshot-Button — macht die Quest systemseitig).
**NEU statt 14:** **Foto-Notiz** — an eine räumliche Notiz ein Foto anhängen.
Achtung Technik: App-Screenshots enthalten KEIN Passthrough (das Kamerabild
komponiert das OS außerhalb des Eye-Buffers); echte Fotos brauchen die Meta
Passthrough Camera API (experimentell, eigene Permission, HorizonOS v74+).

**Update (2026-07-11):** 1 (Multi-AP-Scan) UMGESETZT (M29) — ohne ADB,
store-tauglich: passives Mitlesen der System-Scans (getScanResults-Cache,
Timestamp-Dedupe), distanzbasiertes Aktiv-Scan-Budget (4/2 min), adaptives
Tempo (startScan-false → Backoff); alle Netze ≥ −90 dBm, nur Heatmap/Minimap
(keine Punktwolken-Kugeln), Panel-Toggle im Aufnahme-Tab, AP-Auswahl als
Dropdown.

**Update (2026-07-12):** 13 (Panel greifen) und 15 (Palette-Langdruck) sind
UMGESETZT: Das Hauptpanel lässt sich mit dem rechten Grip am Pointer greifen
und frei ablegen; anschließend dreht es sich beim Herumlaufen aufrecht zum
Nutzer mit. Langes Drücken auf Öffnung/Notiz/Ping öffnet ein Optionsblatt
an der Hand (Tür/Fenster, Notiztyp, persistenter Ping-Host), ohne anschließend
zusätzlich den normalen Werkzeug-Klick auszulösen.

**Update (2026-07-12):** 4 (Durchsatz-Knoten) ist UMGESETZT als lokaler,
browsergestützter Test direkt über das Quest-Web-Dashboard: adaptive parallele
HTTP-Messung Quest→Browser und Browser→Quest, kein `receiver.py`, räumlicher
Knoten mit BSSID/Band/Latenz, Live-Anzeige sowie Session-/JSON-/Report-Integration.

**Zurückgestellt:** 5 (Latenz-Dauermessung),
Foto-Notiz (neu), 21 (Elektro-/Medienplan).

## A) Messung & Analyse

1. **★ Multi-AP-Scan (alle sichtbaren Netze):** Bisher wird nur der VERBUNDENE
   AP gemessen. Mit `WifiManager.getScanResults()` alle BSSIDs samt RSSI/Kanal
   erfassen; Heatmap per Panel-Dropdown auf eine SSID/BSSID umschaltbar.
   *Technische Hürde:* Android drosselt aktive Scans (~4 Scans/2 min im
   Vordergrund) — Konzept: passive Ergebnisse nutzen + Drossel-Countdown im
   Panel anzeigen. Größter funktionaler Sprung Richtung echtem Site-Survey.
2. **★ Roaming-Marker:** BSSID-Wechsel des verbundenen APs automatisch als
   Marker in den Laufpfad setzen (vorhandene WorldLabel/Notiz-Infrastruktur).
   Zeigt sofort, WO das Headset zwischen APs/Bändern springt — klassische
   Fehlerquelle bei Mesh-Netzen. Geringer Aufwand.
3. **Band-/Kanal-Tagging:** `WifiInfo.frequency` pro Sample mitschreiben
   (Sample-Feld + CSV-Spalte, wie `floor`); Filter „nur 2,4 / nur 5 / nur 6
   GHz“ neben dem RSSI-Filter. Erklärt RSSI-Sprünge durch Band-Steering.
4. **Durchsatz-Knoten (Speedtest) — UMGESETZT (2026-07-12):** Wie PingTester, aber misst ~2 s
   TCP-Download von einer konfigurierbaren URL (z. B. Datei auf dem PC, der eh
   receiver.py laufen hat) → Mbps-Knoten mit Farbband. RSSI ist nicht alles —
   Durchsatz ist die Wahrheit. PingTester ist die fertige Vorlage.
5. **Latenz-Dauermessung:** Zusätzlich zu manuellen Ping-Knoten alle N Sekunden
   automatisch proben und als zweiter Heatmap-Layer (RSSI ⇄ Latenz umschalten)
   rendern. FloorHeatmapRenderer-Compute ist dafür bereits generisch genug.
6. **AP-Lokator v2:** Statt Weighted-Centroid ein Log-Distance-Pfadverlust-Fit
   (Gradientenabstieg über die Samples) + angezeigter Unsicherheitsradius;
   Mehrdeutigkeit sinkt deutlich, sobald um Ecken gemessen wurde.
7. **Wand-Dämpfungs-Schätzung:** Mit gezeichneten Wänden + Messpunkten
   beiderseits einer Wand den mittleren dB-Verlust pro Wand schätzen und als
   Label an die Wand schreiben (nutzt RoomTracer-Geometrie + Samples). Einzigartiges
   Feature, das 2D-Survey-Apps nicht können.
8. **★ Prognose-Heatmap („Was wäre wenn“):** Die vorhandene Notiz „Virtueller
   AP“ aufwerten: einfaches Pfadverlustmodell (Freiraum + Wand-Dämpfung aus den
   gezeichneten Räumen) rechnet eine PROGNOSTIZIERTE Abdeckung als eigenen
   Heatmap-Layer. AP virtuell umhängen, Abdeckung sofort sehen — Kaufargument
   Nr. 1. Baut auf 7. auf, funktioniert aber auch mit Pauschalwert (z. B. 5 dB
   pro Wand).
9. **Session-Vergleich (Diff-Modus):** Zwei geladene Scans (vorher/nachher
   AP-Umbau) als Differenz-Heatmap (grün = besser, rot = schlechter).
   SessionLoader + ScanAligner liefern die deckungsgleiche Basis schon.

## B) Usability & Komfort

10. **★ Haptisches Feedback:** Kurze Controller-Vibration bei: Ecke gerastet,
    Element platziert/gelöscht, Tool gewechselt, „Neue Etage?“-Vorschlag.
    (OpenXR `SendHapticImpulse`.) Kleiner Aufwand, große gefühlte Qualität.
11. **Globales Undo:** Ein Undo-Stack für destruktive Aktionen (Raum/Notiz/
    Messung/Bohrlinie gelöscht, Räume geleert) — Palette-Button „↺“. Die
    Load-APIs aus M22 sind faktisch schon die Restore-Hälfte.
12. **Autosave-Wiederherstellung beim Start:** Wenn `wifi_autosave` existiert,
    beim Start fragen „Letzte Session fortsetzen?“ (SessionLoader existiert —
    nur ein Start-Prompt nötig). Schützt vor Akku-leer/Absturz-Frust.
13. **Panel greifen & verschieben — UMGESETZT (2026-07-12):** Das schwebende Menü am Rand mit Grip
    packen und frei positionieren (statt nur Neu-Beschwören vor dem Nutzer).
14. **Sprachdiktat für Notizen:** Text-Notizen per Android-Spracherkennung
    statt Overlay-Tastatur — in MR deutlich schneller als Tippen.
15. **Palette-Langdruck = Tool-Optionen — UMGESETZT (2026-07-12):** Langer Klick auf ein Palette-Tool
    öffnet Mini-Optionen (Tür/Fenster-Modus, Ping-Host, Notiz-Typ) direkt an
    der Hand, ohne Hauptmenü.
16. **Onboarding-Ablauf:** Beim ersten Start geführte Schritte (Boden
    kalibrieren → Raum zeichnen → Aufnahme starten) mit den vorhandenen
    Hint-Texten als Overlay-Sequenz.
17. **Minimap an Wand pinnen:** Karte per Geste groß an eine Wand projizieren
    (Kopie des Canvas, weltfest) — für Besprechungen zu zweit im Raum.

## C) Export & Reporting

18. **★ Survey-Report (HTML/PDF):** Ein Klick erzeugt einen Bericht:
    Heatmap-PNGs pro Etage + kombinierter Grundriss + RSSI-Statistik +
    Notizen-/Ping-/Messungs-Tabellen + AP-Fix. Als self-contained HTML neben
    die Exporte (WebDashboardPage-Muster) → über `/download` auch am Handy.
    Macht aus Rohdaten ein vorzeigbares Ergebnis.
19. **Screenshot-Button:** MR-Screenshot (Passthrough + Overlays) aus dem Panel
    für Dokumentation direkt in den Export-Ordner.
20. **Etagen-Stapel im Dashboard:** Web-Dashboard: 3D-Ansicht der extrudierten
    Räume aller Etagen übereinander (Daten sind im rooms-Paket schon da).

## D) Gebäude-Dokumentation (später)

21. **Elektro-/Medienplan per Wandplatzierung:** Mit dem Controllerstrahl auf
    eine gezeichnete Wand zeigen und dort normgerechte beziehungsweise klar
    unterscheidbare Symbole platzieren. Unterstützte Elemente:
    * Steckdosen als 1-fach bis n-fach Mehrfachdose,
    * Lichtschalter,
    * Lampenauslässe,
    * Datendosen (Netzwerk),
    * Multimedia-/Koaxialdosen.

    Jedes Element speichert Raum/Wandkante, Abstand entlang der Wand, Montagehöhe,
    Typ, Anzahl und optionale Beschriftung. Es muss mit Session-Laden,
    Scan-Ausrichtung, Etagen, Undo/Löschen und Web-Dashboard synchronisiert werden.
    Der Export erzeugt zusätzlich einen eigenen
    `<stem>_elektroplan[_<Etage>].svg`/PDF-tauglichen Plan mit Symbollegende;
    der bestehende normale Grundriss bleibt unverändert und wird weiterhin
    separat exportiert.

## Empfohlener Einstieg

**2 (Roaming-Marker)** und **10 (Haptik)** als schnelle Gewinne, dann
**18 (Report)** für den „fertigen Produkt“-Eindruck, danach der große Block
**1 (Multi-AP)** → **8 (Prognose)**, der das Tool von „Messgerät“ zu
„Planungswerkzeug“ macht.
