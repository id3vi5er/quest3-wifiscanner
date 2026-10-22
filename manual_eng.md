# Quest 3 WiFi Scanner — English Menu Guide

This guide explains the German in-headset interface and the controller actions needed to operate it. The application starts in mixed-reality passthrough with recording paused.

## 1. Basic interaction

1. Press the **Menu button on the left controller** to show or hide the main panel.
2. Point at the panel with the **right-controller ray** and press the **right trigger** to select buttons, sliders, lists, or text fields.
3. Hold the **right grip while pointing at the panel** to pick it up and move it. After release, the panel automatically turns to face you as you walk around it.
4. Text fields open the Quest system keyboard.
5. The active tab or tool is highlighted. Buttons ending in **`an`** are on; buttons ending in **`aus`** are off.

The panel header always shows:

- `REC` or `PAUSE`: current recording state
- `Ansicht`: visualization mode
- `Punkte`: number of WiFi measurement points
- `RSSI`: current received signal strength in dBm
- `Min / Max / Ø`: minimum, maximum, and average RSSI
- `SSID`: connected WiFi network

## 2. Controller shortcuts

| Controller input | Action |
|---|---|
| Right **A** | Pause or resume recording when no tool is active |
| Right **B** | Export when no tool is active |
| Right trigger | Click UI elements or perform the active tool action |
| Left thumbstick up/down | Change point-cloud and heatmap opacity |
| Right thumbstick up/down | Zoom the wrist minimap; changes drill depth while the drill tool is active |
| Right thumbstick click | Cycle Point Cloud → Heatmap → Both |
| Left **Y** | Show or hide the wrist minimap |
| Left Menu button | Show or hide the main panel |
| Right grip over the panel | Grab and reposition the panel |
| Both grips | Confirm grid calibration or loaded-scan alignment when requested |

An active tool may temporarily take over A, B, the trigger, or the right thumbstick. Its instructions are shown on the left-wrist tool palette.

## 3. Main menu tabs

| German tab | English meaning |
|---|---|
| `Aufnahme` | Recording |
| `Ansicht` | View |
| `Räume` | Rooms |
| `System` | System |

### `Aufnahme` — Recording

| German label | Meaning and use |
|---|---|
| `Export-Name (optional)` | Optional base name for exported files. Leave empty to use an automatic name. |
| `● Aufnahme` | Start or resume recording. |
| `❚❚ Pause` | Pause recording. Existing data remains visible. |
| `Export` | Write the current scan, heatmaps, floor plans, report data, and available sidecar data to device storage. |
| `Scan: …` | Cycle through saved scan names. |
| `Laden` | Load the selected scan. Because this replaces the current session, press it a second time within four seconds to confirm. |
| `Punkte löschen` | Delete the current WiFi measurement samples. This does not perform a full new-session reset. The action can be restored with `Undo` while it remains the latest undoable action. |
| `Neuer Scan` | Start a completely empty session. Recording stops while the confirmation is open and the new session remains paused. |
| `Multi-AP-Scan: an/aus` | Enable or disable collection of visible neighboring access points. Each BSSID receives separate measurements and analysis. |
| `Durchsatz messen (n)` | Ask the open dashboard browser to run a local bidirectional throughput test. `n` is the number of stored throughput points. |
| `Vergleich: …` | Cycle through saved scans that can be used as the before/reference scan. |
| `Diff: an/aus` | Show signal changes against the selected reference. Green means better; red means worse. Align the scans first for a meaningful comparison. |

#### New scan confirmation

After selecting `Neuer Scan`, the panel asks `Aktuelle Session vorher exportieren?` — “Export the current session first?”

| German button | Action |
|---|---|
| `Export + Neu` | Export the current session, then clear everything and create a paused new session. If export fails, the old data is kept. |
| `Ohne Export` | Clear the session without exporting and create a paused new session. |
| `Abbrechen` | Cancel. If recording was active before opening the prompt, it resumes. |

#### Throughput test requirements

The Quest button does not test against an external speed-test service. It measures the local link between the Quest and a browser:

1. Open the `System` tab and make sure `Web-Dashboard: an` is displayed.
2. Open the shown `http://<quest-ip>:8080` address on a PC or phone connected to the same network.
3. Keep the dashboard page open.
4. Stand at the location to be measured and select `Durchsatz messen` in the Quest.
5. Wait for the Quest-to-browser and browser-to-Quest results to appear as a spatial marker.

If the panel says `Erst Webinterface öffnen`, open the dashboard page first. `Web-Dashboard ist aus` means the server must be enabled. Throughput markers can be hidden under `Ansicht` or deleted with the wrist delete tool.

### `Ansicht` — View

| German label | Meaning and use |
|---|---|
| `Ansicht: Punktwolke/Heatmap/Beides` | Cycle between point cloud, floor heatmap, and both. |
| `Occlusion: an/aus` | Let real objects hide virtual scan graphics using Quest environment depth. |
| `Raumscan: an/aus` | Start or stop dynamic depth integration for the generated room mesh. |
| `Raummesh: sichtbar/verborgen` | Show or hide the generated room mesh without stopping its scan. |
| `Minimap: an/aus` | Show or hide the map attached to the left wrist. |
| `Grundriss: an/aus` | Show or hide the traced floor plan on the minimap. |
| `Karte: Norden fest` | Keep the minimap fixed to the scan/grid direction. Press again to use the rotating view mode. |
| `AP-Standort: an/aus` | Show or hide estimated access-point locations and uncertainty indicators. |
| `AP: Alle ▾` | Open the AP list. Choose all access points or one SSID/BSSID and band. The heatmap and AP markers follow this selection. |
| `Roaming: an/aus` | Show or hide recorded BSSID/band-change events. |
| `Durchsatzpunkte: an/aus (n)` | Show or hide spatial throughput markers without deleting them. |
| `Audio: an/aus` | Enable or disable Geiger-style signal-strength audio feedback. |
| `Prognose: an/aus` | Show or hide the predicted coverage map based on rooms, walls, openings, and the selected AP. |
| `Deckkraft` | Opacity of the point cloud and the currently displayed heatmap/coverage layer. |
| `Filter (dBm)` | Set the minimum and maximum RSSI values shown. Use it, for example, to isolate weak areas below approximately -75 dBm. |

`Alle APs` means “All APs”. New BSSIDs discovered by Multi-AP scanning appear in this list automatically.

### `Räume` — Rooms

The room tab manages calibration, saved geometry, and floors. Actual placement tools are on the left-wrist palette described later.

| German label | Meaning and use |
|---|---|
| `Grid ausrichten` | Arm grid alignment. Hold both controllers along a real wall, hold both grips, check the preview line, then release to apply. Select `Grid ausrichten` again to cancel while armed. |
| `Boden kalibrieren` | Arm floor calibration. Aim the orange cursor at the real floor and press the right trigger. |
| `Räume löschen` | Delete all traced rooms. Press a second time within four seconds when `Sicher? Nochmal` appears. |
| `Öffnung entfernen` | Remove the most recently placed door or window opening. |
| `Raum benennen` | Aim into a completed room and press the trigger to open the keyboard and assign a name. |
| `Messungen löschen` | Delete all distance measurements. |
| `Notizen löschen` | Delete all spatial notes. |
| `Ping löschen` | Delete all ping nodes. |
| `Wand-Dämpfung: an/aus` | Show or hide estimated wall attenuation values. |
| `Etage: … ▾` | Select one floor or `Alle Etagen` (all floors). |
| `Etage anlegen` | Create a floor at the current head height when appropriate. |
| `▲ Etage darüber` | Add a floor above the current floor. |
| `▼ Etage darunter` | Add a floor below the current floor. |
| `Bezeichnung ▲/▼` | Shift all floor labels up or down, for example when the initial level should be OG1 instead of EG. |

Common floor abbreviations:

- `EG` — ground floor
- `OG1`, `OG2`, … — first, second, … upper floor
- `KG1`, `KG2`, … — basement levels
- `Alle Etagen` — all floors

### `System` — System

| German label | Meaning and use |
|---|---|
| `PC-IP für Live-Stream` | Optional destination IP for the legacy UDP PC receiver. It is not required for the built-in web dashboard. Invalid addresses are rejected. |
| `Web-Dashboard: an/aus` | Start or stop the Quest-hosted dashboard. When enabled, the URL is displayed below the button. |
| `Scans löschen (Gerät)` | Permanently delete all saved export files from the Quest. Press a second time within four seconds to confirm. This is different from clearing only the current session. |
| `Tutorial starten` | Restart the in-headset tutorial. |

To use the dashboard, connect the viewing device to the same local network and open the displayed address, normally:

```text
http://<quest-ip>:8080
```

## 4. Left-wrist tool palette

Look at the left wrist and use the right-controller ray to select a tool. Only one placement tool can be active at a time.

| German tool | English name | Operation |
|---|---|---|
| `Raum` | Room | Right trigger places corners. B finishes the room. A removes the last unfinished corner. Aim at a completed corner and hold the trigger to move it. Aim at the floor inside a completed room and hold the trigger while moving vertically to set wall height. |
| `Öffnung` | Opening | Places doors or windows in traced walls. A switches type, B removes the last opening. Hold the palette button to choose `Tür` (door) or `Fenster` (window) directly. |
| `Bohren` | Drill | Aim at a wall, floor, or ceiling and press the trigger to place a drill line. Right stick up/down changes depth. A removes the last drill line. |
| `Distanz` | Distance | Press the trigger for the start point, move to the end point, then press again. Multiple measurements can remain visible. |
| `Notiz` | Note | Press the trigger to place a note at the controller. A cycles note type. Hold the palette button to choose `AP`, `Störung` (interference/problem), or `Text`. |
| `Ping` | Ping | Press the trigger to place a node and measure latency. Hold the palette button to enter a host name or IP address. |
| `Löschen` | Delete | Aim at a room, drill line, distance measurement, note, ping node, or throughput point. The target highlights. Press the trigger twice to confirm deletion. |
| `Ausrichten` | Align | Align a loaded scan with the real building. See the alignment workflow below. |
| `✕ Aus` | Tools off | Deactivate the current tool. |
| `↺ Undo` | Undo | Undo the latest supported destructive or placement action. The button is dimmed when no undo is available. |

### Placing doors and windows

The room must already have wall height before openings can be placed.

**Door (`Tür`):**

1. Aim at a traced wall.
2. First trigger press: set the upper corner on the hinge side.
3. Second trigger press on the same wall: set the door width. The opening extends down to the floor automatically.

**Window (`Fenster`):**

1. Aim at a traced wall.
2. Press the trigger at the first corner.
3. Press again at the opposite corner on the same wall.

### Aligning a loaded scan

Use this after loading a session because the Quest tracking origin may differ between launches.

1. Activate `Ausrichten` on the wrist palette.
2. Aim at a wall in the loaded floor plan and press the trigger. A green sphere marks the selected anchor corner. If no wall can be selected, set room height first.
3. Place the left controller at the matching real-world anchor corner.
4. Point the right controller along the matching real wall.
5. Hold both grips and inspect the preview.
6. Release the grips to apply the rotation and translation.

Press B to go back or cancel. Samples, rooms, floors, and grid orientation move together.

## 5. Recommended first-scan workflow

1. Put the Quest and the dashboard PC/phone on the WiFi network being surveyed.
2. Open the menu and select `Neuer Scan` if old session data is present.
3. Use `Boden kalibrieren` if the detected floor height is incorrect.
4. Optionally use `Grid ausrichten` along a main wall.
5. Trace rooms with the `Raum` wrist tool and add wall height, doors, and windows if floor-plan exports or coverage prediction are needed.
6. Select the correct floor under `Räume` → `Etage`.
7. Return to `Aufnahme` and select `● Aufnahme` or press A.
8. Walk slowly through the building. Keep the headset tracking stable and avoid recentering during the scan.
9. Pause recording when finished.
10. Enter an optional export name and select `Export` or press B.
11. Enable the web dashboard under `System` to inspect or download the results.

## 6. Quick translation reference

| German | English |
|---|---|
| `an` / `aus` | on / off |
| `sichtbar` / `verborgen` | visible / hidden |
| `Aufnahme` | recording |
| `Ansicht` | view |
| `Punkte` | points |
| `Räume` | rooms |
| `Etage` | floor/storey |
| `Grundriss` | floor plan |
| `Laden` | load |
| `Löschen` | delete |
| `Sicher? Nochmal` | Are you sure? Press again |
| `Abbrechen` | cancel |
| `fehlgeschlagen` | failed |
| `bereit (Pause)` | ready (paused) |
| `Alle` | all |
| `zurück` | back/undo last step |
| `Störung` | interference/problem |
| `Wand-Dämpfung` | wall attenuation |
| `Durchsatz` | throughput |
| `Prognose` | coverage prediction |

## 7. Safety notes

- `Ohne Export` in the new-scan prompt discards the current session data.
- `Scans löschen (Gerät)` deletes all saved scan files, not only the active scan.
- Loading a scan replaces the current in-memory session; export first if it must be preserved.
- Use the tools only where you can safely move and reach in passthrough. Keep the physical room clear and remain aware of stairs, walls, doors, and other people.
