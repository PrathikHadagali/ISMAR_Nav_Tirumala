# HARI-AR Unity Client

AR navigation client for the Tirumala temple complex. Talks to the Python
backend in `N - Copy/` and renders a landmark-anchored AR pathway.

Unity **6000.3.15f1**, URP, Android/ARCore.

## Quick start

1. **Packages** — `HARI-AR ▸ Setup ▸ Add AR Packages`
   (AR Foundation, ARCore XR Plugin, XR Management, Newtonsoft JSON)
2. **Optional Geospatial** — `HARI-AR ▸ Setup ▸ Add ARCore Geospatial`
   Needs git on PATH. If it fails, the app still runs on GPS + compass.
3. **Configure** — `HARI-AR ▸ Setup ▸ Configure Everything`
   Sets Android player settings, writes `AndroidManifest.xml`, creates
   `Assets/Scenes/HariAR_Navigation.unity`.
4. **Point at the backend** — open the scene, select the `HARI-AR` object, set
   **Backend Url** to your machine's LAN IP, e.g. `http://192.168.1.42:8000`.
   Not `localhost` — that resolves to the phone.
5. **Verify** — `HARI-AR ▸ Check Setup`
6. **Build And Run** to an ARCore-capable Android device.

Start the backend bound to all interfaces so the phone can reach it:

```bash
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

## Architecture

One component, `HariArApp`, builds the whole client at runtime — AR rig,
services, rendering, navigation, UI, study harness. The saved scene contains a
single GameObject. Nothing is wired by hand, so the configuration lives in
source control and cannot be broken by a stray inspector edit.

```
Assets/Scripts/HariAR/
├── HariArApp.cs                composition root — builds everything
├── Core/
│   ├── GeoUtils.cs             mirror of the backend's app/core/geo.py
│   ├── NavModels.cs            DTOs matching the backend JSON exactly
│   └── NavApiClient.cs         UnityWebRequest transport
├── Localization/
│   ├── GpsProvider.cs          accuracy gate, speed gate, adaptive smoothing
│   ├── HeadingProvider.cs      compass + AR yaw complementary filter
│   └── GeoAnchorManager.cs     lat/lon → Unity world, drift re-sync
├── AR/
│   ├── RouteRenderer.cs        the AR pathway ribbon
│   ├── WaypointMarker.cs       chevrons + destination beacon + content manager
│   └── LandmarkLabel.cs        world-anchored landmark names  ← the RQ2 payload
├── Navigation/
│   └── NavigationController.cs FSM + 1 Hz progress heartbeat + re-routing
├── Voice/
│   ├── SpeechInput.cs          Android RecognizerIntent, tap-to-toggle
│   └── TtsPlayer.cs            on-device TTS, backend MP3 fallback
├── UI/
│   └── InstructionHUD.cs       instructions, arrow, mic, destination browser
└── Study/
    ├── StudyController.cs      baseline vs HARI-AR conditions
    └── StudyLogger.cs          CSV telemetry
```

## How the AR pathway is placed

The backend returns two tiers and the client uses each for its own job:

| Backend field | Client use |
|---|---|
| `path[]` (~3 m) | ground ribbon mesh, rebuilt within 60 m of the user |
| `anchors[]` (~15 m, ≤60) | chevron markers + destination beacon |
| `steps[].landmark` | world-anchored landmark labels |

Placement needs an origin and a north reference:

```
enu   = ToEnu(lat, lon, originLat, originLon)
world = originWorld + Rot(-northOffset) · (enu.east, 0, enu.north)
```

`northOffset` comes from `HeadingProvider`, which fuses the magnetometer
(absolute but noisy near iron and crowds) with AR camera yaw (steady but
drifting). The origin is re-synced to GPS when the two disagree by more than
8 m, blended over 1.5 s so the pathway slides instead of teleporting.

## Localization modes

| Mode | When | Accuracy |
|---|---|---|
| `Geospatial` | ARCore Extensions installed **and** VPS coverage present | ~1 m |
| `GpsCompass` | everywhere else — the default | 3–9 m |

Geospatial code is behind the `HARIAR_GEOSPATIAL` define, added automatically
by the setup menu when the package installs. VPS coverage on the Tirumala
hilltop is unverified, so the fallback is the path that must work.

## GPS handling

The paper names 3–9 m drift as the defining site problem. Three defences in
`GpsProvider`:

1. **Accuracy gate** — discard fixes reporting worse than 30 m.
2. **Speed gate** — discard fixes implying faster than walking pace (the
   classic multipath signature: jump 40 m, come straight back).
3. **Adaptive smoothing** — filter strength follows reported accuracy, so good
   fixes pass through and poor ones are damped.

Raw and filtered positions are both logged, so the study can quantify what the
filtering actually bought.

## Study mode

Set `Enable Study Mode` on `HariArApp`, then set participant id, task (1–4),
and condition.

| Condition | Shows |
|---|---|
| `Baseline` | compass arrow + distance only |
| `HariAr` | arrow + AR pathway + chevrons + landmark labels + spoken landmarks |

Both conditions use the **same backend route**, so the only variable is the
guidance presentation — which is what makes the comparison valid.
`StudyController.FirstCondition` counterbalances by participant id parity.

Logs land in `Application.persistentDataPath/study/`:

- `P01_T1_HariAr_<timestamp>_track.csv` — one row per GPS fix (raw + filtered
  position, accuracy, heading, cross-track error, distance walked)
- `..._events.csv` — route received, step changes, wayfinding errors, arrival

Pull them off the device with:

```bash
adb shell "run-as com.hariar.navigation ls files/study"
```

Call `StudyController.MarkWayfindingError()` when the observer sees a wrong
turn, and `MarkTurnDecision(junction, correct)` at each decision point — these
produce the §4.4 error rate and the §4.3 turn-accuracy comparison.

## Editor testing without a device

`Simulate Location In Editor` feeds a fixed Tirumala coordinate (GNC Tollgate
by default) so the pipeline can be exercised in Play mode. The AR camera will
not track and the ribbon will not appear, but routing, instructions, HUD, and
the destination browser all work against the live backend.

## Troubleshooting

| Symptom | Cause |
|---|---|
| "Cannot reach the navigation server" | `backendUrl` is localhost, or uvicorn is not bound to `0.0.0.0` |
| Camera black | ARCore not installed on device, or Graphics API not GLES3 |
| Pathway points the wrong way | Compass uncalibrated — figure-8 the phone; check `showDiagnostics` for heading reliability |
| Pathway drifts while walking | Expected without VPS; watch `resyncs` in diagnostics |
| Labels but no ribbon | No horizontal plane detected yet — point the camera at the ground |
