# Quest 3 AR Wi-Fi Scanner – Features

Willkommen zur Feature-Übersicht des **Quest 3 Wi-Fi Scanners**! Diese App verwandelt die Meta Quest 3 in ein professionelles, räumliches Netzwerkanalyse-Werkzeug. Anstatt auf flache 2D-Graphen auf einem Smartphone zu starren, machst du WLAN-Signale in deiner echten Umgebung **sichtbar**.

Egal ob für IT-Administratoren, Heimnetzwerk-Optimierer oder als faszinierende Tech-Demo für Mixed Reality – diese Software kombiniert modernste AR-Technologien mit praxisnaher Datenanalyse.

---

## Haupt-Features im Überblick

### Immersive Mixed Reality (Passthrough)
* **Full-Color Passthrough:** Du siehst deine reale Umgebung gestochen scharf und in Farbe, während die digitalen WLAN-Daten nahtlos darüber eingeblendet werden.
* **Boundaryless Mode:** Keine störenden Wände oder Guardian-Grenzen im Weg! Du kannst dich frei durch das ganze Haus bewegen, von Raum zu Raum, ohne durch künstliche Spielfeldgrenzen eingeschränkt zu werden.

### Räumliche Signal-Visualisierung
WLAN-Empfangsstärke (RSSI) wird live in den Raum gezeichnet. Du hast die Wahl, wie du die Daten sehen möchtest:
* **3D Point Cloud (Punktwolke):** Hinterlässt einen farbigen Pfad aus Messpunkten direkt in der Luft, genau dort, wo du gelaufen bist.
* **Boden-Heatmap:** Projiziert ein farbiges Raster direkt auf den Boden deines Raumes. Kalte Farben (Blau/Grün) stehen für schwachen Empfang, warme Farben (Rot/Orange) für starke Signale.
* **Kombi-Modus:** Zeigt Punktwolke und Heatmap gleichzeitig für maximale Übersicht.

### Dynamische Verdeckung (Dynamic Depth Occlusion)
Dank der revolutionären Depth API der Quest 3 verschmelzen die virtuellen Daten perfekt mit der Realität:
* Virtuelle Messpunkte und die Heatmap **verschwinden realistisch hinter echten Objekten** (Möbel, Wände, Personen).
* Es ist **kein vorheriger Raum-Scan nötig**. Der Tiefensensor der Quest 3 berechnet die Verdeckung in Echtzeit!
* (Optional) **Geist-Modus:** Lasse die echten Wände virtuell anzeigen, basierend auf dem Meta Space Setup.

### Orientierung & Navigation
Behalte auch in großen Gebäuden immer den Überblick:
* **Interaktive Handgelenk-Minimap:** Auf deinem rechten Handgelenk befindet sich eine Live-Karte. Sie zeigt den komplett vermessenen Bereich (Boden-Heatmap), deine aktuelle Position als Pfeil und deine Blickrichtung. Die Karte wächst automatisch mit, je mehr Fläche du erkundest.
* **Live-HUD (Heads-Up Display):** Auf dem linken Handgelenk hast du alle wichtigen Echtzeit-Parameter im Blick: Aktuelles Netzwerk (SSID), Signalstärke (RSSI in dBm), Anzahl der Messpunkte und Aufnahme-Status.

### Architektur-Tools: Raum-Zeichner (Room Tracer)
Erstelle maßstabsgetreue Grundrisse direkt in AR:
* **Manueller Zeichen-Modus:** Klicke mit dem Controller einfach die Ecken deiner echten Räume an. Die App verbindet sie zu einem virtuellen Grundriss.
* **Minimap-Overlay:** Die gezeichneten Wände erscheinen sofort als weiße Hilfslinien auf deiner Handgelenk-Minimap, damit du genau siehst, wo noch Funklöcher im Raum sein könnten.
* **SVG Grundriss-Export:** Alle gezeichneten Räume werden als gemeinsame, **maßstabsgetreue Vektorgrafik (.svg)** exportiert (1 Unit = 1mm). Die Wände sind bereits praktisch in Metern beschriftet!

### Präzisions-Werkzeuge & Bedienung
* **Wand-Kalibrierung (Grid Align):** Richte das Raster der Heatmap mit wenigen Klicks parallel zu deinen echten Wänden aus, damit die exportierten Karten perfekt gerade sind.
* **RSSI-Filter:** Du suchst nur nach Funklöchern? Blende starke Signale per Schieberegler einfach aus und zeige nur Werte unter z. B. -75 dBm an.
* **Floating Control Panel:** Ein virtuelles Tablet, das vor dir schwebt. Hier steuerst du Deckkraft, Ansichtsmodi, Export-Namen (via Systemtastatur) und startest/stoppst die Aufnahme.

### Umfassender Daten-Export
Wenn der Scan abgeschlossen ist, bleiben die Daten nicht in der Brille. Speichere deine Messergebnisse lokal auf der Quest und ziehe sie per PC-Skript (`pull_scans.ps1`) bequem auf deinen Rechner:
* **SVG:** Maßstabsgetreuer 2D-Grundriss der gezeichneten Räume.
* **PNG:** Hochauflösendes 2D-Bild der Boden-Heatmap.
* **OBJ:** Die komplette WLAN-Punktwolke als 3D-Modell (z.B. für Blender oder Unity).
* **CSV:** Alle rohen Messpunkte (Zeit, X/Y/Z-Koordinaten, RSSI-Wert) für die Datenanalyse in Excel, Python oder R.
* **JSON:** Strukturierte Rohdaten (inkl. Raum-Eckpunkten) zur einfachen maschinellen Weiterverarbeitung.

---
*Erlebe WLAN wie nie zuvor – unsichtbare Wellen werden greifbare Realität.*